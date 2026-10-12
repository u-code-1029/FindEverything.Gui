using FindEverything.Application.FileSearch;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class FileSearchQuerySessionTests
{
    [Fact]
    public async Task Opening_a_captured_query_rejects_a_new_generation_before_reading_any_rows()
    {
        var service = new RecordingService([]);
        IFileSearchService api = service;
        var oldGeneration = Guid.NewGuid();

        await Assert.ThrowsAsync<IndexSnapshotChangedException>(() => api.OpenQueryAsync(service.Scope,
            new EntrySearchRequest(service.Scope.RootPath) { ExpectedScanId = oldGeneration }));
        Assert.Empty(service.Requests);
    }

    [Theory]
    [InlineData(EntrySortDirection.Ascending)]
    [InlineData(EntrySortDirection.Descending)]
    public async Task Explicit_selection_uses_exact_path_batches_and_preserves_global_order(EntrySortDirection direction)
    {
        var rows = Enumerable.Range(0, 2105).Select(index => Entry(index)).ToArray();
        var ordered = direction == EntrySortDirection.Ascending ? rows : rows.Reverse().ToArray();
        var service = new RecordingService(ordered);
        IFileSearchService api = service;
        await using var query = await api.OpenQueryAsync(service.Scope, new EntrySearchRequest(service.Scope.RootPath)
        {
            ExpectedScanId = service.Generation,
            SortBy = EntrySortField.Name,
            SortDirection = direction,
        });
        // Deliberately interleave keys so each server-sorted batch crosses others.
        var selected = rows.Where((_, index) => index % 2 == 0).Select(row => row.FullPath).ToArray();
        var selection = new QuerySelection(keys: selected.OrderBy(path => path.Length).ThenByDescending(path => path));
        var actual = new List<string>();
        await foreach (var row in query.EnumerateSelectionAsync(selection)) actual.Add(row.FullPath);

        Assert.Equal(ordered.Where(row => selected.Contains(row.FullPath)).Select(row => row.FullPath), actual);
        Assert.All(service.Requests, request =>
        {
            Assert.InRange(request.FullPaths.Count, 1, 1000);
            Assert.Equal(service.Generation, request.ExpectedScanId);
            Assert.False(request.IncludeTotalCount);
            Assert.InRange(request.Limit, 1, 50);
        });
    }

    [Fact]
    public async Task Selection_is_captured_before_enumeration_continues_and_never_mixes_generations()
    {
        var rows = Enumerable.Range(0, 501).Select(Entry).ToArray();
        var service = new RecordingService(rows);
        IFileSearchService api = service;
        await using var query = await api.OpenQueryAsync(service.Scope, new EntrySearchRequest(service.Scope.RootPath));
        var selection = new QuerySelection(allMatching: true);
        await using var enumerator = query.EnumerateSelectionAsync(selection).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        selection.Clear();
        for (var index = 1; index < 500; index++) Assert.True(await enumerator.MoveNextAsync());
        service.Generation = Guid.NewGuid();

        await Assert.ThrowsAsync<IndexSnapshotChangedException>(async () => { await enumerator.MoveNextAsync(); });
    }

    private static IndexedPathEntry Entry(int index)
    {
        var root = Path.Combine(Path.GetTempPath(), "query-test-root");
        var name = $"entry-{index:D5}.txt";
        return new(Path.Combine(root, name), name, root, IndexedPathKind.File, index,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false);
    }

    private sealed class RecordingService(IReadOnlyList<IndexedPathEntry> rows) : IFileSearchService
    {
        public FileSearchScope Scope { get; } = new(Path.Combine(Path.GetTempPath(), "query-test-root"), "unused.db");
        public Guid Generation { get; set; } = Guid.NewGuid();
        public List<EntrySearchRequest> Requests { get; } = [];
        public Task<IndexRootStatus> GetStatusAsync(FileSearchScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IndexRootStatus(scope.RootPath, IndexRootAvailability.Available, Generation,
                scope.RootPath, IndexScanStatus.Completed, DateTimeOffset.UnixEpoch, rows.Count, 0, false));
        public Task<EntrySearchResult> SearchAsync(FileSearchScope scope, EntrySearchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ExpectedScanId != Generation) throw new IndexSnapshotChangedException(scope.RootPath,
                request.ExpectedScanId, Generation);
            Requests.Add(request);
            var matching = request.FullPaths.Count == 0 ? rows : rows.Where(row => request.FullPaths.Contains(row.FullPath)).ToArray();
            var offset = request.AfterCursor is null ? request.Offset : int.Parse(request.AfterCursor);
            var page = matching.Skip(offset).Take(request.Limit).ToArray();
            var hasMore = offset + page.Length < matching.Count;
            return Task.FromResult(new EntrySearchResult(page, matching.Count, hasMore, false)
            {
                NextCursor = hasMore ? (offset + page.Length).ToString() : null,
                LastScanId = Generation,
            });
        }
        public Task<IndexScanReport> ScanAsync(FileSearchScope scope, IProgress<IndexScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IndexScanReport> RefreshScopeAsync(FileSearchScope scope, string refreshPath,
            IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
