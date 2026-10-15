using System.Runtime.CompilerServices;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;

namespace FindEverything.Desktop.Services;

/// <summary>A fixed checked subset of an unfiltered catalog, with native SQL paging.</summary>
internal sealed class SelectedCatalogQuerySession(IResultQuerySession<CatalogProjectionRow> inner,
    QuerySelection selection) : IResultQuerySession<CatalogProjectionRow>, IResultSelectionKeyReader
{
    private readonly QuerySelection _selection = selection.Capture();
    public string Identity => inner.Identity;
    public Task<long> CountAsync(CancellationToken cancellationToken = default) => inner.CountSelectionAsync(_selection, cancellationToken);
    public Task<ResultPage<CatalogProjectionRow>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
        inner.ReadSelectionRangeAsync(_selection, offset, limit, cancellationToken);

    public async IAsyncEnumerable<CatalogProjectionRow> EnumerateSelectionAsync(QuerySelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var captured = selection.Capture();
        var buffer = new List<CatalogProjectionRow>(500);
        await foreach (var row in inner.EnumerateSelectionAsync(_selection, cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(row);
            if (buffer.Count < 500) continue;
            var flags = await captured.ReadMembershipAsync(buffer.Select(item => item.StableKey ?? item.FullPath).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < buffer.Count; index++) if (flags[index]) yield return buffer[index];
            buffer.Clear();
        }
        if (buffer.Count > 0)
        {
            var flags = await captured.ReadMembershipAsync(buffer.Select(item => item.StableKey ?? item.FullPath).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < buffer.Count; index++) if (flags[index]) yield return buffer[index];
        }
    }

    public async IAsyncEnumerable<ResultSelectionKey> EnumerateSelectionKeysAsync(QuerySelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var captured = selection.Capture();
        if (inner is not IResultSelectionKeyReader reader)
        {
            await foreach (var row in EnumerateSelectionAsync(captured, cancellationToken).ConfigureAwait(false))
                yield return new(row.StableKey ?? row.FullPath, row.FullPath);
            yield break;
        }
        var buffer = new List<ResultSelectionKey>(500);
        await foreach (var key in reader.EnumerateSelectionKeysAsync(_selection, cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(key);
            if (buffer.Count < 500) continue;
            var flags = await captured.ReadMembershipAsync(buffer.Select(item => item.StableKey).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < buffer.Count; index++) if (flags[index]) yield return buffer[index];
            buffer.Clear();
        }
        if (buffer.Count > 0)
        {
            var flags = await captured.ReadMembershipAsync(buffer.Select(item => item.StableKey).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < buffer.Count; index++) if (flags[index]) yield return buffer[index];
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await inner.DisposeAsync().ConfigureAwait(false); }
        finally { _selection.Dispose(); }
    }
}
