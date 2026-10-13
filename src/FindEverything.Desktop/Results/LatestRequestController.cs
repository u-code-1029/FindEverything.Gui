namespace FindEverything.Desktop.Results;

/// <summary>Owns passive, supersedable work without coupling it to a scan lifetime.</summary>
internal sealed class LatestRequestController : IDisposable
{
    private CancellationTokenSource? _active;
    private bool _disposed;

    public async Task RunAsync(TimeSpan delay, Func<CancellationToken, Task> work)
    {
        if (_disposed) return;
        Cancel();
        using var source = new CancellationTokenSource();
        _active = source;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, source.Token);
            source.Token.ThrowIfCancellationRequested();
            await work(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_active, source)) _active = null; }
    }

    public void Cancel() { _active?.Cancel(); _active = null; }
    public void Dispose() { _disposed = true; Cancel(); }
}
