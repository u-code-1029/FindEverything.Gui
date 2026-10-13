using System.Runtime.CompilerServices;
using System.Text;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;

namespace FindEverything.Application.FileSearch;

internal sealed class FileSearchQuerySession : IResultQuerySession<IndexedPathEntry>
{
    private readonly IFileSearchService _service;
    private readonly FileSearchScope _scope;
    private readonly EntrySearchRequest _query;
    private readonly Dictionary<int, string> _cursors = [];
    private readonly object _gate = new();
    private Task<long>? _count;
    private bool _disposed;

    private FileSearchQuerySession(IFileSearchService service, FileSearchScope scope,
        EntrySearchRequest query, Guid? scanId)
    {
        _service = service;
        _scope = scope;
        _query = query with { Offset = 0, AfterCursor = null, ExpectedScanId = scanId };
        Identity = Guid.NewGuid().ToString("N");
    }

    public string Identity { get; }

    public static async Task<IResultQuerySession<IndexedPathEntry>> OpenAsync(
        IFileSearchService service, FileSearchScope scope, EntrySearchRequest query,
        CancellationToken cancellationToken)
    {
        var status = await service.GetStatusAsync(scope, cancellationToken).ConfigureAwait(false);
        if (status.Availability != IndexRootAvailability.Available)
            throw new FileSearchIndexUnavailableException(scope.RootPath);
        // Export/copy opens an independent session for the displayed generation.
        // Never silently replace its requested generation with the newest one.
        if (query.ExpectedScanId is { } expected && expected != status.LastScanId)
            throw new IndexSnapshotChangedException(scope.RootPath, expected, status.LastScanId);
        return new FileSearchQuerySession(service, scope, query, status.LastScanId);
    }

    public Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_count is { IsCanceled: true } or { IsFaulted: true }) _count = null;
            return _count ??= ReadCountAsync(cancellationToken);
        }
    }

    private async Task<long> ReadCountAsync(CancellationToken token)
    {
        var page = await _service.SearchAsync(_scope,
            _query with { Offset = 0, Limit = 1, IncludeTotalCount = true }, token)
            .ConfigureAwait(false);
        return page.TotalCount;
    }

    public async Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(offset));
        string? cursor;
        lock (_gate) _cursors.TryGetValue(offset, out cursor);
        var page = await _service.SearchAsync(_scope,
            _query with
            {
                Offset = cursor is null ? offset : 0,
                AfterCursor = cursor,
                Limit = limit,
                IncludeTotalCount = false,
            }, cancellationToken).ConfigureAwait(false);
        if (page.NextCursor is not null)
        {
            lock (_gate) _cursors[offset + page.Entries.Count] = page.NextCursor;
        }
        return new ResultPage<IndexedPathEntry>(page.Entries, offset, page.HasMore);
    }

    public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(
        QuerySelection selection, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var captured = selection.Capture();
        if (!captured.AllMatching && captured.Baseline is null)
        {
            await foreach (var item in EnumerateExplicitAsync(captured.Keys, cancellationToken).ConfigureAwait(false))
                yield return item;
            yield break;
        }
        for (var offset = 0; ;)
        {
            var page = await ReadRangeAsync(offset, 500, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (captured.Contains(item.FullPath)) yield return item;
            }
            if (!page.HasMore) yield break;
            if (page.Items.Count == 0) throw new InvalidDataException("Empty continuation page.");
            offset += page.Items.Count;
        }
    }

    private async IAsyncEnumerable<IndexedPathEntry> EnumerateExplicitAsync(
        IReadOnlyCollection<string> keys, [EnumeratorCancellation] CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (keys.Count == 0) yield break;
        var batches = keys.Chunk(1000).Select(paths => new SelectionBatch(paths)).ToArray();
        // The batch count grows with explicit selected keys, not the source DB.
        // Bound buffered row objects even for a large shift-selection.
        var pageSize = Math.Clamp(5000 / batches.Length, 1, 50);
        var queue = new PriorityQueue<SelectionBatch, SelectionSortKey>(new SelectionSortComparer(_query));
        foreach (var batch in batches)
        {
            var first = await ReadSelectionBatchAsync(batch, pageSize, token).ConfigureAwait(false);
            if (first is not null) queue.Enqueue(batch, SelectionSortKey.Create(first, _query.SortBy));
        }

        while (queue.TryDequeue(out var batch, out _))
        {
            token.ThrowIfCancellationRequested();
            yield return batch.Items[batch.Index - 1];
            var next = await ReadSelectionBatchAsync(batch, pageSize, token).ConfigureAwait(false);
            if (next is not null) queue.Enqueue(batch, SelectionSortKey.Create(next, _query.SortBy));
        }
    }

    private async Task<IndexedPathEntry?> ReadSelectionBatchAsync(SelectionBatch batch, int limit, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (batch.Index < batch.Items.Count) return batch.Items[batch.Index++];
        if (!batch.HasMore) return null;
        var page = await _service.SearchAsync(_scope, _query with
        {
            FullPaths = batch.Paths,
            Offset = batch.Offset,
            AfterCursor = batch.Cursor,
            Limit = limit,
            IncludeTotalCount = false,
        }, token).ConfigureAwait(false);
        batch.Items = page.Entries;
        batch.Index = 0;
        batch.HasMore = page.HasMore;
        batch.Cursor = page.NextCursor;
        batch.Offset = page.NextCursor is null ? batch.Offset + page.Entries.Count : 0;
        if (page.Entries.Count == 0)
        {
            if (page.HasMore) throw new InvalidDataException("Empty continuation page.");
            return null;
        }
        return batch.Items[batch.Index++];
    }

    private sealed class SelectionBatch(string[] paths)
    {
        public string[] Paths { get; } = paths;
        public IReadOnlyList<IndexedPathEntry> Items { get; set; } = [];
        public int Index { get; set; }
        public int Offset { get; set; }
        public string? Cursor { get; set; }
        public bool HasMore { get; set; } = true;
    }

    private sealed record SelectionSortKey(byte[] Text, long Number, bool IsNull, IndexedPathKind Kind, byte[] PathKey)
    {
        public static SelectionSortKey Create(IndexedPathEntry entry, EntrySortField field)
        {
            var text = field switch
            {
                EntrySortField.Name => entry.Name.ToLowerInvariant(),
                EntrySortField.Path => entry.FullPath.ToLowerInvariant(),
                EntrySortField.FileType => entry.Kind == IndexedPathKind.Directory
                    ? string.Empty : Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant(),
                _ => string.Empty,
            };
            var number = field switch
            {
                EntrySortField.Size => entry.SizeBytes ?? 0,
                EntrySortField.Created => entry.CreatedUtc.UtcTicks,
                EntrySortField.Modified => entry.ModifiedUtc.UtcTicks,
                EntrySortField.Kind => (int)entry.Kind,
                _ => 0,
            };
            var pathKey = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.FullPath));
            if (OperatingSystem.IsWindows()) pathKey = pathKey.ToUpperInvariant();
            return new(Encoding.UTF8.GetBytes(text), number, entry.SizeBytes is null,
                entry.Kind, Encoding.UTF8.GetBytes(pathKey));
        }
    }

    // Mirrors the engine's invariant-lower + UTF-8 BINARY SQL ordering. In
    // particular, null sizes stay last and type sorting keeps folders first.
    private sealed class SelectionSortComparer(EntrySearchRequest query) : IComparer<SelectionSortKey>
    {
        public int Compare(SelectionSortKey? left, SelectionSortKey? right)
        {
            if (left is null || right is null) return left is null ? right is null ? 0 : -1 : 1;
            int primary;
            if (query.SortBy == EntrySortField.Size && left.IsNull != right.IsNull)
                return left.IsNull.CompareTo(right.IsNull);
            if (query.SortBy == EntrySortField.FileType && left.Kind != right.Kind)
                return right.Kind.CompareTo(left.Kind);
            primary = query.SortBy is EntrySortField.Name or EntrySortField.Path or EntrySortField.FileType
                ? left.Text.AsSpan().SequenceCompareTo(right.Text)
                : left.Number.CompareTo(right.Number);
            if (query.SortDirection == EntrySortDirection.Descending) primary = -primary;
            return primary != 0 ? primary : left.PathKey.AsSpan().SequenceCompareTo(right.PathKey);
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_gate) _cursors.Clear();
        return ValueTask.CompletedTask;
    }
}
