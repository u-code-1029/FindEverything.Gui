namespace FindEverything.Application.Catalog;

public sealed record CatalogProgressBatch(CatalogOperationProgress Latest,
    IReadOnlyList<CatalogOperationProgress> Matches);

/// <summary>Count-based UI reporting; persistence still receives every mapped row.</summary>
public sealed class CatalogProgressBatcher(int batchSize, Action<CatalogProgressBatch> publish,
    int maximumLiveItems = 500) : IProgress<CatalogOperationProgress>
{
    private readonly object _gate = new();
    private readonly List<CatalogOperationProgress> _matches = [];
    private readonly Dictionary<CatalogOperationPhase, long> _publishedCounts = [];
    private CatalogOperationProgress? _latest;
    private int _attempt = 1;
    private int _liveCount;
    private bool _closed;

    public void Report(CatalogOperationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CatalogProgressBatch? batch = null;
        lock (_gate)
        {
            if (_closed || value.AttemptId < _attempt) return;
            if (value.AttemptReset || value.AttemptId > _attempt)
            {
                _attempt = value.AttemptId;
                _matches.Clear(); _publishedCounts.Clear(); _liveCount = 0;
            }
            _latest = value;
            if (value.MatchedItem is not null && _liveCount++ < maximumLiveItems) _matches.Add(value);
            if (value.AttemptReset || value.Phase == CatalogOperationPhase.Preparing
                || _matches.Count >= Math.Max(1, batchSize)
                || value.ProcessedItems - _publishedCounts.GetValueOrDefault(value.Phase) >= Math.Max(1, batchSize))
                batch = TakeBatch();
        }
        if (batch is not null) publish(batch);
    }

    public void FlushAndClose()
    {
        CatalogProgressBatch? batch;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            batch = _latest is not null ? TakeBatch() : null;
        }
        if (batch is not null) publish(batch);
    }

    private CatalogProgressBatch TakeBatch()
    {
        var latest = _latest!;
        var result = new CatalogProgressBatch(latest, _matches.ToArray());
        _publishedCounts[latest.Phase] = latest.ProcessedItems;
        _latest = null; _matches.Clear();
        return result;
    }
}

/// <summary>Coalesces queued UI updates without dropping matched rows or retry resets.</summary>
public sealed class CatalogProgressDelivery(Action<Action> dispatch, Action<CatalogProgressBatch> apply)
{
    private readonly object _gate = new();
    private readonly List<CatalogOperationProgress> _matches = [];
    private CatalogOperationProgress? _latest;
    private bool _reset;
    private bool _scheduled;

    public void Post(CatalogProgressBatch batch)
    {
        lock (_gate)
        {
            if (batch.Latest.AttemptReset) { _matches.Clear(); _reset = true; }
            _latest = batch.Latest with { MatchedItem = null };
            _matches.AddRange(batch.Matches);
            if (_scheduled) return;
            _scheduled = true;
        }
        dispatch(Drain);
    }

    private void Drain()
    {
        CatalogProgressBatch batch;
        lock (_gate)
        {
            batch = new(_latest! with { AttemptReset = _reset }, _matches.ToArray());
            _latest = null; _matches.Clear(); _reset = false; _scheduled = false;
        }
        apply(batch);
    }
}
