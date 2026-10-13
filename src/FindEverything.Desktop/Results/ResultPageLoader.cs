using FindEverything.Application.Results;

namespace FindEverything.Desktop.Results;

/// <summary>Storage and row formatting share one worker boundary for first and later pages.</summary>
internal sealed class ResultPageLoader<TRow, TItem>(IResultQuerySession<TRow> query, Func<TRow, TItem> map)
{
    public async Task<ResultPage<TItem>> ReadAsync(int offset, int limit, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            var page = await query.ReadRangeAsync(offset, limit, token).ConfigureAwait(false);
            var items = new TItem[page.Items.Count];
            for (var index = 0; index < items.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                items[index] = map(page.Items[index]);
            }
            return new ResultPage<TItem>(items, page.Offset, page.HasMore);
        }, token).WaitAsync(token).ConfigureAwait(false);
    }
}
