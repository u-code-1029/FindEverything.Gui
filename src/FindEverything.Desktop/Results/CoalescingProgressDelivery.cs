namespace FindEverything.Desktop.Results;

/// <summary>At most one UI callback is queued; counters use the latest producer value.</summary>
internal sealed class CoalescingProgressDelivery<T> : IProgress<T>, IDisposable where T : class
{
    private readonly object _gate = new();
    private readonly Action<Action> _dispatch;
    private readonly Action<T> _apply;
    private readonly Timer _timer;
    private readonly TimeSpan _interval;
    private T? _latest;
    private bool _scheduled;
    private bool _posted;
    private bool _closed;

    public CoalescingProgressDelivery(Action<Action> dispatch, Action<T> apply, TimeSpan? interval = null)
    {
        _dispatch = dispatch; _apply = apply;
        _interval = interval ?? TimeSpan.FromMilliseconds(100);
        _timer = new Timer(_ => PostDrain(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            if (_closed) return;
            _latest = value;
            if (_scheduled) return;
            _scheduled = true;
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    private void PostDrain()
    {
        lock (_gate)
        {
            if (_posted || _latest is null) return;
            _posted = true;
        }
        _dispatch(Drain);
    }

    private void Drain()
    {
        T? latest;
        lock (_gate)
        {
            latest = _latest; _latest = null;
            _scheduled = false; _posted = false;
        }
        if (latest is not null) _apply(latest);
    }

    public void FlushAndClose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _timer.Dispose();
        }
        PostDrain();
    }

    public void Dispose() => FlushAndClose();
}
