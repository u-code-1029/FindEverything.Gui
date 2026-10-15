using System.IO;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task File_activity_badge_tracks_scans_not_cached_reads_and_clears_on_disposal() => OnStaAsync(() =>
    {
        var activity = new CatalogActivityState { IsScanning = true };
        var database = Path.Combine(Path.GetTempPath(), "file-activity-fixture.db");
        using var owner = new FileSearchViewModel(new FileService(), new IndexingSettings(), new Workspace(database),
            new DatabaseResolver(database), null!, null!,
            new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
            new Notifier(), new Snackbar(), new Formats(), null!, null!, new Profiles(), null!,
            new TestAppLocalizer(), NullLogger<FileSearchViewModel>.Instance, activityState: activity);

        owner.IsSearching = true;
        Assert.False(activity.IsFilesScanning);
        owner.IsBusy = true;
        Assert.True(activity.IsFilesScanning);
        owner.IsSearching = false;
        Assert.True(activity.IsFilesScanning);
        owner.IsBusy = false;
        Assert.False(activity.IsFilesScanning);
        owner.IsBusy = true;
        owner.Dispose();
        Assert.False(activity.IsFilesScanning);
        Assert.True(activity.IsScanning); // The catalog's activity is independent.
        return Task.CompletedTask;
    });
}
