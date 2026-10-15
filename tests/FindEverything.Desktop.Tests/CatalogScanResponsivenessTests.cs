using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Scan_preview_adds_rows_without_resetting_the_view_and_discards_them_on_abort(bool fails) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var owner = fixture.ViewModel;
        Writer? writer = null;
        fixture.Store.WriterFactory = (scope, fields) => writer = new Writer(scope, fields);
        var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainingRows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allRowsWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ScanMappingProbe();
        var dispatcherThread = Environment.CurrentManagedThreadId;
        fixture.Service.StreamingScanOverride = async (request, progress, token) =>
        {
            void Emit(int first, int last)
            {
                for (var index = first; index <= last; index++)
                {
                    var row = new CatalogItem(Path.Combine(request.RootPath, $"row-{index}"), $"row-{index}", "rule",
                        new object(), new Dictionary<string, object?> { ["client"] = probe }, false);
                    request.ResultWriter!.Append(row);
                    progress!.Report(new(CatalogOperationPhase.Mapping, "mapping", index)
                    { MatchedItem = row, MatchedCount = index });
                }
            }
            Emit(1, 10);
            await secondBatch.Task.WaitAsync(token);
            Emit(11, 20);
            await remainingRows.Task.WaitAsync(token);
            Emit(21, 1_000);
            allRowsWritten.TrySetResult();
            await finish.Task.WaitAsync(token);
            throw new IOException("Simulated scan failure.");
        };

        owner.IsDetailsOpen = true;
        var scanning = owner.ScanCommand.ExecuteAsync(null);
        try
        {
            await WaitForAsync(() => owner.Items.Count == 10);
            var view = owner.Items;
            var resets = 0;
            var additions = 0;
            ((INotifyCollectionChanged)view).CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
                if (args.Action == NotifyCollectionChangedAction.Add) additions++;
            };
            Assert.NotEqual(dispatcherThread, probe.MappingThread);
            Assert.False(owner.IsResultInteractionEnabled);
            Assert.False(owner.IsDetailsOpen);
            Assert.False(owner.ToggleDetailsCommand.CanExecute(null));
            Assert.False(owner.SelectAllCommand.CanExecute(null));
            Assert.False(owner.ClearSelectionCommand.CanExecute(null));
            Assert.True(owner.CancelCommand.CanExecute(null));

            // Even a direct command/property call cannot trigger filter or
            // selection work while scanning. The latest keyword is for completion.
            owner.FilterText = "not-in-the-preview";
            owner.ApplyColumnFilter(new(CatalogColumnTarget.Field("client"), CatalogFilterOperator.Contains) { Text = "other" });
            owner.ApplyColumnSort(CatalogColumnTarget.Field("client"), true);
            var firstRow = owner.Items.Cast<CatalogItemViewModel>().First();
            await owner.SelectRowAsync(firstRow, additive: true, range: false);
            Assert.False(firstRow.IsSelected);
            Assert.Empty(owner.ActiveColumnFilters);
            Assert.False(owner.IsColumnSorted(CatalogColumnTarget.Field("client")));
            Assert.Equal(string.Empty, owner.AppliedHighlightQuery);

            secondBatch.TrySetResult();
            await WaitForAsync(() => owner.Items.Count == 20);
            Assert.Same(view, owner.Items);
            Assert.Equal(10, additions);
            Assert.Equal(0, resets);

            remainingRows.TrySetResult();
            await allRowsWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForAsync(() => owner.Items.Count == 500);
            Assert.Equal(1_000, writer!.Count); // Persistence receives every match, not just the preview.
            Assert.Same(view, owner.Items);
            Assert.Equal(0, resets);
            Assert.Equal(490, additions);

            if (fails) finish.TrySetResult();
            else owner.CancelCommand.Execute(null);
            await scanning.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(owner.Items.Cast<object>());
            Assert.False(owner.HasLoadedItems);
            Assert.False(owner.IsBusy);
            Assert.True(owner.IsResultInteractionEnabled);
        }
        finally
        {
            if (!scanning.IsCompleted) owner.CancelCommand.Execute(null);
            await scanning.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task Scan_completion_applies_the_latest_deferred_keyword() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.StreamingScanOverride = async (request, progress, token) =>
        {
            for (var index = 1; index <= 10; index++)
            {
                var row = new CatalogItem(Path.Combine(request.RootPath, $"row-{index}"), $"row-{index}", "rule",
                    new object(), new Dictionary<string, object?> { ["client"] = "Example" }, false);
                request.ResultWriter!.Append(row);
                progress!.Report(new(CatalogOperationPhase.Mapping, "mapping", index)
                { MatchedItem = row, MatchedCount = index });
            }
            await complete.Task.WaitAsync(token);
            return new CatalogResult(new SampleProfile().Descriptor, [], [], 10, 0, false, null) { MatchedCount = 10 };
        };
        var scan = fixture.ViewModel.ScanCommand.ExecuteAsync(null);
        try
        {
            await WaitForAsync(() => fixture.ViewModel.Items.Count == 10);
            fixture.ViewModel.FilterText = "latest keyword";
            Assert.Equal(0, fixture.Store.OpenQueryCalls);
            Assert.Equal(string.Empty, fixture.ViewModel.AppliedHighlightQuery);
            complete.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("latest keyword", fixture.Store.LastQuery!.Keyword);
            Assert.Equal("latest keyword", fixture.ViewModel.AppliedHighlightQuery);
            Assert.Equal(1, fixture.Store.OpenQueryCalls);
            Assert.True(fixture.ViewModel.IsResultInteractionEnabled);
        }
        finally
        {
            if (!scan.IsCompleted) fixture.ViewModel.CancelCommand.Execute(null);
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task Repeated_live_counters_do_not_requery_or_reset_unchanged_preview_eligibility() => OnStaAsync(() =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var owner = fixture.ViewModel;
        var fields = new SampleProfile().Descriptor.Fields;
        var scope = new CatalogProjectionScope("live", "sample", Path.GetTempPath(), "live.db", "fingerprint", "ko-KR", null);
        using var writer = new Writer(scope, fields);
        writer.Append(new(Path.Combine(Path.GetTempPath(), "sample"), "sample", "rule", new object(), new Dictionary<string, object?>(), false));
        owner.ApplyColumnFilter(new(CatalogColumnTarget.Field("client"), CatalogFilterOperator.Contains) { Text = "Example" });
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(CatalogViewModel);
        var version = (long)type.GetField("_resultSessionVersion", privateInstance)!.GetValue(owner)!;
        var capture = type.GetMethod("CaptureLiveFilterSnapshot", privateInstance)!;
        var schedule = type.GetMethod("ScheduleLiveFilterRefresh", privateInstance)!;
        var resets = 0;
        ((INotifyCollectionChanged)owner.Items).CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
        capture.Invoke(owner, [writer, scope, fields, version]);
        schedule.Invoke(owner, [false]);
        for (var count = 0; count < 100; count++) schedule.Invoke(owner, [false]);
        Assert.Equal(1, fixture.Store.LiveFilterCalls);
        Assert.Equal(0, resets);

        // Rows delivered after the stage snapshot still require one eligibility
        // read, even when no new database revision was published meanwhile.
        type.GetField("_livePreviewVersion", privateInstance)!.SetValue(owner, 1L);
        schedule.Invoke(owner, [false]);
        Assert.Equal(2, fixture.Store.LiveFilterCalls);
        Assert.Equal(0, resets);
        return Task.CompletedTask;
    });

    private sealed class ScanMappingProbe
    {
        public int MappingThread { get; private set; }
        public override string ToString() { MappingThread = Environment.CurrentManagedThreadId; return "Example"; }
    }
}
