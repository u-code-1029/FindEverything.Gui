namespace FindEverything.Application.Indexing;

public sealed class IndexProgressBatcher(int batchSize, IProgress<IndexScanProgress> target)
    : IProgress<IndexScanProgress>
{
    private IndexScanProgress? _latest;
    private long _publishedEntries;
    private bool _closed;

    public void Report(IndexScanProgress value)
    {
        if (_closed) return;
        _latest = value;
        if (value.Entries - _publishedEntries >= Math.Max(1, batchSize)) Publish();
    }

    public void FlushAndClose()
    {
        if (_closed) return;
        _closed = true;
        if (_latest is not null) Publish();
    }

    private void Publish()
    {
        var value = _latest!;
        _latest = null; _publishedEntries = value.Entries;
        target.Report(value);
    }
}
