using System.Runtime.CompilerServices;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;

namespace FindEverything.Desktop.Services;

/// <summary>Only loaded/selected keys are translated; all filtering stays in the derived engine DB.</summary>
internal sealed class ImportedFileQuerySession(IResultQuerySession<IndexedPathEntry> inner,
    ImportedWorkspaceSource source) : IResultQuerySession<IndexedPathEntry>
{
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
        var translated = new QuerySelection(selection.AllMatching,
            selection.Keys.Select(key => WorkspacePathMapping.ToEffective(source, key)));
        await foreach (var row in inner.EnumerateSelectionAsync(translated, cancellationToken).ConfigureAwait(false))
            yield return AttachSource(row);
    }
    private IndexedPathEntry AttachSource(IndexedPathEntry row)
    {
        var original = WorkspacePathMapping.ToOriginal(source, row.FullPath);
        return row with { OriginalFullPath = original, StableKey = original.ToUpperInvariant() };
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
