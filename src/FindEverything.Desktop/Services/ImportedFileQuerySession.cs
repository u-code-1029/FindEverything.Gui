using System.Runtime.CompilerServices;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Infrastructure.Persistence;

namespace FindEverything.Desktop.Services;

/// <summary>Only loaded/selected keys are translated; all filtering stays in the derived engine DB.</summary>
internal sealed class ImportedFileQuerySession(IResultQuerySession<IndexedPathEntry> inner,
    ImportedWorkspaceSource source, IDisposable? ownedLease = null) : IResultQuerySession<IndexedPathEntry>
{
    private IDisposable? _generationLease = ownedLease ?? ImportedGenerationLease.Acquire(source.MetadataDatabasePath);
    public static async Task<IResultQuerySession<IndexedPathEntry>> OpenAsync(
        Func<Task<IResultQuerySession<IndexedPathEntry>>> open, ImportedWorkspaceSource? source)
    {
        if (source is null) return await open().ConfigureAwait(false);
        var lease = ImportedGenerationLease.Acquire(source.MetadataDatabasePath);
        try { return new ImportedFileQuerySession(await open().ConfigureAwait(false), source, lease); }
        catch { lease.Dispose(); throw; }
    }
    public string Identity => $"{source.Id}:{source.LinkRevision}:{inner.Identity}";
    public Task<long> CountAsync(CancellationToken cancellationToken = default) => inner.CountAsync(cancellationToken);
    public async Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        var page = await inner.ReadRangeAsync(offset, limit, cancellationToken).ConfigureAwait(false);
        return page with { Items = page.Items.Select(AttachSource).ToArray() };
    }
    public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var translated = selection.MapKeys(key => WorkspacePathMapping.ToEffective(source, key));
        await foreach (var row in inner.EnumerateSelectionAsync(translated, cancellationToken).ConfigureAwait(false))
            yield return AttachSource(row);
    }
    private IndexedPathEntry AttachSource(IndexedPathEntry row)
    {
        var original = WorkspacePathMapping.ToOriginal(source, row.FullPath);
        return row with { OriginalFullPath = original, StableKey = original.ToUpperInvariant() };
    }
    public async ValueTask DisposeAsync()
    {
        try { await inner.DisposeAsync().ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _generationLease, null)?.Dispose(); }
    }
}
