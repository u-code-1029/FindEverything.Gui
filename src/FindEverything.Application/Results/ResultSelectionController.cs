namespace FindEverything.Application.Results;

/// <summary>
/// Owns the lifetime of a selection gesture. A range is prepared away from the
/// caller, then adopted atomically on the caller's context if still current.
/// The owner calls Cancel before bulk selection or changing its query.
/// </summary>
public sealed class ResultSelectionController(QuerySelection selection) : IDisposable
{
    private CancellationTokenSource? _request;
    private readonly HashSet<CancellationTokenSource> _additiveRequests = [];
    private long _version;
    private bool _disposed;

    public void Cancel()
    {
        _version++;
        _request?.Cancel();
        _request = null;
        foreach (var request in _additiveRequests.ToArray()) request.Cancel();
    }

    public async Task<bool> SelectAsync<T>(IResultQuerySession<T> query, string key,
        int index, int anchor, bool additive, bool range, Func<T, string> keySelector,
        Func<bool> isCurrent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (additive && (!range || anchor < 0 || index < 0) && selection.Baseline is { } baseline)
            return await ToggleBaselineAsync(baseline, key, isCurrent);
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
                        var keys = page.Items.Select(keySelector).ToArray();
                        var baseline = captured.Baseline is { } source
                            ? await source.ReadMembershipAsync(keys, cancellation.Token).ConfigureAwait(false) : null;
                        foreach (var itemKey in keys)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            captured.SetResolvedMembership(itemKey, true, baseline?.Contains(itemKey) ?? false);
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

    private async Task<bool> ToggleBaselineAsync(IQuerySelectionKeySet baseline, string key, Func<bool> isCurrent)
    {
        // A new individual click replaces an unfinished range, but it must not
        // discard other individual clicks whose membership reads are pending.
        if (_request is not null) Cancel();
        var version = _version;
        using var cancellation = new CancellationTokenSource();
        using var lease = baseline.Retain();
        _additiveRequests.Add(cancellation);
        try
        {
            var membership = await lease.ReadMembershipAsync([key], cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || version != _version || !ReferenceEquals(baseline, selection.Baseline) || !isCurrent()) return false;
            var member = membership.Contains(key);
            // Compute the toggle against current state, not a pre-await copy.
            // Two rapid clicks on the same row therefore still cancel out.
            selection.SetResolvedMembership(key, !selection.ContainsResolvedMembership(key, member), member);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        finally { _additiveRequests.Remove(cancellation); }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); }
}
