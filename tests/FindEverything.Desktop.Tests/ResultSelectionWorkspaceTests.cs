using System.IO;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task A_row_checked_while_the_next_filter_loads_is_not_counted_as_selected_in_that_filter() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true);
        var owner = fixture.ViewModel; await owner.LoadCommand.ExecuteAsync(null);
        var oldRow = Assert.Single(owner.Items.Cast<CatalogItemViewModel>());
        var next = new Query(ignoreCountCancellation: false, cacheCount: 1)
        {
            RowName = "other-filter-row",
            ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        fixture.Store.ReplacementQuery = next;
        owner.FilterText = "other"; owner.ApplySearchImmediately();
        await next.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.SelectRowAsync(oldRow, additive: true, range: false);
        next.ReadGate.TrySetResult();
        await WaitForAsync(() => !owner.IsSearchPending && owner.CurrentResultsSelectionState == false);
        Assert.Equal(1, owner.SelectedCount);
        Assert.False(Assert.Single(owner.Items.Cast<CatalogItemViewModel>()).IsSelected);
        owner.ToggleCurrentResultsSelectionCommand.Execute(null);
        await WaitForAsync(() => !owner.IsPreservingSelection && owner.SelectedCount == 2);
    });

    [Fact]
    public Task Inspecting_original_values_does_not_requery_or_change_checked_scope() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true);
        var owner = fixture.ViewModel; await owner.LoadCommand.ExecuteAsync(null);
        await owner.SelectRowAsync(Assert.Single(owner.Items.Cast<CatalogItemViewModel>()), additive: true, range: false);
        owner.ShowOriginalValues = true;
        Assert.Equal(1, fixture.Store.OpenQueryCalls);
        Assert.Equal(1, owner.SelectedCount);
        fixture.Store.ReplacementQuery = new Query(ignoreCountCancellation: false, cacheCount: 1);
        owner.FilterText = "Example"; owner.ApplySearchImmediately();
        await WaitForAsync(() => fixture.Store.OpenQueryCalls == 2 && !owner.IsSearchPending);
        Assert.False(fixture.Store.LastQuery!.ShowOriginalValues);
        Assert.Equal(1, owner.SelectedCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task File_checks_survive_search_changes_and_header_clear_only_clears_current_results(bool selectAll) => OnStaAsync(async () =>
    {
        var service = new FileService(); service.Query.ReleaseCount.TrySetResult(1);
        using var owner = CreateFilesOwner(service); await owner.ActivateAsync();
        if (selectAll) owner.SelectAllCommand.Execute(null);
        else await owner.SelectRowAsync(Assert.Single(owner.Items), additive: true, range: false);
        Assert.Equal(1, owner.SelectedCount);
        service.Query = new FileQuery { RootPath = Path.Combine(Path.GetTempPath(), "different-filter") };
        service.Query.ReleaseCount.TrySetResult(1);
        owner.SearchText = "another"; owner.ApplySearchImmediately();
        await WaitForAsync(() => service.OpenCalls >= 2 && !owner.IsSearching && !owner.IsPreservingSelection);
        Assert.Equal(1, owner.SelectedCount);
        Assert.False(Assert.Single(owner.Items).IsSelected);
        await owner.SelectRowAsync(Assert.Single(owner.Items), additive: true, range: false);
        Assert.Equal(2, owner.SelectedCount);
        await WaitForAsync(() => owner.CurrentResultsSelectionState == true);
        owner.ToggleCurrentResultsSelectionCommand.Execute(null);
        await WaitForAsync(() => !owner.IsPreservingSelection && owner.SelectedCount == 1 && !Assert.Single(owner.Items).IsSelected);
        Assert.False(Assert.Single(owner.Items).IsSelected);
        owner.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, owner.SelectedCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Catalog_checks_survive_search_changes_and_enter_skips_debounce(bool selectAll) => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true);
        var owner = fixture.ViewModel; await owner.LoadCommand.ExecuteAsync(null);
        if (selectAll) owner.SelectAllCommand.Execute(null);
        else await owner.SelectRowAsync(Assert.Single(owner.Items.Cast<CatalogItemViewModel>()), additive: true, range: false);
        fixture.Store.ReplacementQuery = new Query(ignoreCountCancellation: false, cacheCount: 0);
        owner.FilterText = "does-not-match";
        Assert.True(owner.IsSearchPending);
        owner.ApplySearchImmediately();
        await WaitForAsync(() => fixture.Store.OpenQueryCalls >= 2 && !owner.IsSearchPending);
        Assert.Equal(1, owner.SelectedCount);
        Assert.Empty(owner.Items.Cast<CatalogItemViewModel>());
        Assert.True(owner.HasSelection);
        owner.ClearSelectionCommand.Execute(null);
        Assert.False(owner.HasSelection);
    });
}
