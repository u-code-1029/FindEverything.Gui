using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogProgressBatcherTests
{
    [Fact]
    public void File_scan_progress_uses_the_same_setting_and_flushes_the_last_partial_batch()
    {
        var values = new List<IndexScanProgress>();
        var progress = new IndexProgressBatcher(10, new RecordingIndexProgress(values));
        for (var i = 1; i <= 9; i++) progress.Report(new(i, i, 0, 0, 0, TimeSpan.Zero, 0));
        Assert.Empty(values);
        progress.Report(new(10, 10, 0, 0, 0, TimeSpan.Zero, 0));
        Assert.Equal(10, Assert.Single(values).Entries);
        progress.Report(new(11, 11, 0, 0, 0, TimeSpan.Zero, 0));
        progress.FlushAndClose();
        Assert.Equal(11, values[1].Entries);
    }

    private sealed class RecordingIndexProgress(List<IndexScanProgress> values) : IProgress<IndexScanProgress>
    { public void Report(IndexScanProgress value) => values.Add(value); }

    [Fact]
    public void Ten_items_update_once_and_completion_flushes_remainder()
    {
        var batches = new List<CatalogProgressBatch>();
        var progress = new CatalogProgressBatcher(10, batches.Add);
        for (var i = 1; i <= 9; i++) progress.Report(Match(i));
        Assert.Empty(batches);
        progress.Report(Match(10));
        Assert.Equal(10, Assert.Single(batches).Matches.Count);
        progress.Report(Match(11));
        Assert.Single(batches);
        progress.FlushAndClose();
        Assert.Equal(11, batches[1].Latest.ProcessedItems);
        Assert.Single(batches[1].Matches);
        progress.Report(Match(12));
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void No_matches_still_update_after_configured_candidate_count()
    {
        var batches = new List<CatalogProgressBatch>();
        var progress = new CatalogProgressBatcher(3, batches.Add);
        for (var i = 1; i <= 3; i++) progress.Report(Match(i) with { MatchedItem = null });
        Assert.Empty(Assert.Single(batches).Matches);
        Assert.Equal(3, batches[0].Latest.ProcessedItems);
    }

    [Fact]
    public void Live_rows_are_bounded_and_retry_discards_previous_attempt()
    {
        var batches = new List<CatalogProgressBatch>();
        var progress = new CatalogProgressBatcher(10, batches.Add, maximumLiveItems: 10);
        for (var i = 1; i <= 25; i++) progress.Report(Match(i));
        progress.FlushAndClose();
        Assert.Equal(10, batches.Sum(batch => batch.Matches.Count));
        batches.Clear();
        progress = new CatalogProgressBatcher(10, batches.Add);
        progress.Report(Match(1));
        progress.Report(Match(0) with { MatchedItem = null, AttemptId = 2, AttemptReset = true });
        progress.Report(Match(9)); // Late old attempt.
        progress.FlushAndClose();
        Assert.Empty(Assert.Single(batches).Matches);
        Assert.True(batches[0].Latest.AttemptReset);
    }

    [Fact]
    public void Pending_ui_updates_coalesce_without_losing_rows_or_retry_reset()
    {
        var pending = new Queue<Action>();
        var batches = new List<CatalogProgressBatch>();
        var delivery = new CatalogProgressDelivery(pending.Enqueue, batches.Add);
        delivery.Post(new(Match(10), [Match(10)]));
        delivery.Post(new(Match(20), [Match(20)]));
        Assert.Single(pending);
        pending.Dequeue()();
        Assert.Equal(2, Assert.Single(batches).Matches.Count);
        Assert.Null(batches[0].Latest.MatchedItem);
        delivery.Post(new(Match(30), [Match(30)]));
        delivery.Post(new(Match(0) with { AttemptReset = true }, []));
        delivery.Post(new(Match(10), [Match(10)]));
        pending.Dequeue()();
        Assert.True(batches[1].Latest.AttemptReset);
        Assert.Single(batches[1].Matches);
    }

    private static CatalogOperationProgress Match(int count) => new(CatalogOperationPhase.Mapping, "progress", count)
    {
        MatchedCount = count,
        MatchedItem = new CatalogItem("/example/" + count, "" + count, "rule", new object(), new Dictionary<string, object?>(), false),
    };
}
