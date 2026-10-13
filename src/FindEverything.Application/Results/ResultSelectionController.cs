namespace FindEverything.Application.Results;

/// <summary>
/// Owns the lifetime of a selection gesture. A range is prepared away from the
/// caller, then adopted atomically on the caller's context if still current.
/// The owner calls Cancel before bulk selection or changing its query.
/// </summary>
public sealed class ResultSelectionController(QuerySelection selection) : IDisposable
{
    private CancellationTokenSource? _request;
    private long _version;
    private bool _disposed;

    public void Cancel()
    {
        _version++;
        _request?.Cancel();
        _request = null;
    }

    public async Task<bool> SelectAsync<T>(IResultQuerySession<T> query, string key,
        int index, int anchor, bool additive, bool range, Func<T, string> keySelector,
        Func<bool> isCurrent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        var version = _version;
        if ((!range || anchor < 0 || index < 0) && selection.Baseline is null)
        {
            if (!isCurrent()) return false;
            if (!additive) selection.Clear();
            selection.Set(key, !additive || !selection.Contains(key));
            return true;
        }
        using var cancellation = new CancellationTokenSource();
        _request = cancellation;
        using var captured = additive ? selection.Capture() : new QuerySelection();
        try
        {
            await Task.Run(async () =>
            {
                if (range && anchor >= 0 && index >= 0)
                {
                    var end = Math.Max(index, anchor);
                    for (var start = Math.Min(index, anchor); start <= end; start += 500)
                    {
                        var page = await query.ReadRangeAsync(start, Math.Min(500, end - start + 1), cancellation.Token)
                            .ConfigureAwait(false);
                        foreach (var item in page.Items)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            captured.Set(keySelector(item), true);
                        }
                    }
                }
                else captured.Set(key, !additive || !captured.Contains(key));
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || version != _version || !isCurrent()) return false;
            selection.ReplaceWith(captured);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        finally { if (ReferenceEquals(_request, cancellation)) _request = null; }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); }
}
