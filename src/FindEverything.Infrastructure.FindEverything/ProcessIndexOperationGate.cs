namespace FindEverything.Infrastructure.FindEverything;

/// <summary>
/// Keeps a multi-page read on one stable in-process index generation and prevents
/// a scan from publishing between pages. The engine's writer lock remains the
/// cross-process authority.
/// </summary>
internal static class ProcessIndexOperationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(Gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
