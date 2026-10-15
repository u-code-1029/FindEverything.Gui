using System.IO;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task File_scan_pauses_result_actions_without_clearing_rows_or_selection() => OnStaAsync(async () =>
    {
        var service = new FileService();
        service.Query.ReleaseCount.TrySetResult(1);
        using var owner = CreateFilesOwner(service);
        await owner.ActivateAsync();
        var row = Assert.Single(owner.Items);
        await owner.SelectRowAsync(row, additive: true, range: false);
        Assert.True(row.IsSelected);
        owner.IsDetailsOpen = true;
        owner.IsFilterOpen = true;
        owner.IsBusy = true;
        Assert.False(owner.IsResultInteractionEnabled);
        Assert.False(owner.CanSearch);
        Assert.False(owner.IsDetailsOpen);
        Assert.False(owner.IsFilterOpen);
        Assert.True(owner.CancelCommand.CanExecute(null));
        Assert.False(owner.SelectAllCommand.CanExecute(null));
        Assert.False(owner.InvertSelectionCommand.CanExecute(null));
        Assert.False(owner.CopySelectionCommand.CanExecute(null));
        Assert.False(owner.ClearSelectionCommand.CanExecute(null));
        owner.ClearSelectionCommand.Execute(null); // Also protect direct command callers.
        await owner.SelectRowAsync(row, additive: true, range: false);
        owner.ApplySort(EntrySortField.Modified, EntrySortDirection.Descending);
        Assert.Equal(EntrySortField.Name, owner.SelectedSort.Value);
        Assert.Same(row, Assert.Single(owner.Items));
        Assert.True(row.IsSelected);
        Assert.Equal(1, owner.SelectedCount);
        Assert.Equal(1, service.OpenCalls);
        owner.IsBusy = false;
        Assert.True(owner.IsResultInteractionEnabled);
        Assert.True(owner.CanSearch);
        Assert.True(owner.CopySelectionCommand.CanExecute(null));
        owner.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, owner.SelectedCount);
        Assert.False(row.IsSelected);
    });

    [Fact]
    public Task File_sort_field_and_direction_change_open_one_query_not_two() => OnStaAsync(async () =>
    {
        var service = new FileService();
        service.Query.ReleaseCount.TrySetResult(1);
        using var owner = CreateFilesOwner(service);
        await owner.ActivateAsync();
        Assert.Equal(1, service.OpenCalls);
        service.Query = new FileQuery();
        service.Query.ReleaseCount.TrySetResult(1);
        owner.ApplySort(EntrySortField.Modified, EntrySortDirection.Descending);
        await WaitForAsync(() => service.OpenCalls >= 2 && !owner.IsSearching);
        Assert.Equal(2, service.OpenCalls);
        Assert.Equal(EntrySortField.Modified, service.LastRequest!.SortBy);
        Assert.Equal(EntrySortDirection.Descending, service.LastRequest.SortDirection);
    });

    private static FileSearchViewModel CreateFilesOwner(FileService service)
    {
        var database = Path.Combine(Path.GetTempPath(), "file-interaction-fixture.db");
        return new(service, new IndexingSettings(), new Workspace(database), new DatabaseResolver(database), null!, null!,
            new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
            new Notifier(), new Snackbar(), new Formats(), null!, null!, new Profiles(), null!,
            new TestAppLocalizer(), NullLogger<FileSearchViewModel>.Instance);
    }
}
