using System.IO;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Legacy_missing_root_preserves_completed_rows_and_selection_before_starting_a_new_session(bool load) => OnStaAsync(async () =>
    {
        var database = Path.Combine(Path.GetTempPath(), "legacy-preflight-fixture.db");
        var service = new CatalogService
        {
            ScanOverride = (request, _) => Task.FromResult(new CatalogResult(new SampleProfile().Descriptor,
                [new CatalogItem(Path.Combine(request.RootPath, "completed"), "completed", "rule", new object(),
                    new Dictionary<string, object?> { ["client"] = "Completed" }, false)], [], 1, 0, false, null)
                { MatchedCount = 1 })
        };
        using var owner = new CatalogViewModel(service, new Profiles(), new Workspace(database),
            new DatabaseResolver(database), null!, null!,
            new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
            new Notifier(), new Snackbar(), new Formats(), null!, null!, null!,
            new TestAppLocalizer(), NullLogger<CatalogViewModel>.Instance, dispatcher: Dispatcher.CurrentDispatcher);
        await owner.ScanCommand.ExecuteAsync(null);
        var row = Assert.Single(owner.Items.Cast<CatalogItemViewModel>());
        await owner.SelectRowAsync(row, additive: true, range: false);
        Assert.True(row.IsSelected);

        owner.RootPath = Path.Combine(Path.GetTempPath(), "findeverything-missing-" + Guid.NewGuid().ToString("N"));
        var rejected = load ? owner.LoadCommand.ExecuteAsync(null) : owner.ScanCommand.ExecuteAsync(null);
        await rejected.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(owner.HasLoadedItems);
        Assert.Same(row, Assert.Single(owner.Items.Cast<CatalogItemViewModel>()));
        Assert.True(row.IsSelected);
        Assert.Equal(1, owner.SelectedCount);
        Assert.False(owner.IsBusy);
        Assert.False(owner.IsScanning);
        Assert.False(owner.CancelCommand.CanExecute(null));
        Assert.Equal("오류", owner.StatusTitle);
        Assert.DoesNotContain("부분 결과", owner.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, service.ScanCalls);
        Assert.Equal(0, service.LoadCalls);
    });
}
