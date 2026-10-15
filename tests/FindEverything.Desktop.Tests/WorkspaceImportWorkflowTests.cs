using FindEverything.Application.Catalog;
using FindEverything.Application.Sharing;
using FindEverything.Profile.Runtime;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task Home_import_before_catalog_attachment_is_activated_when_catalog_is_created() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture(attachCatalog: false);
        fixture.Transfers.InstalledId = "home-imported";
        await fixture.TransferViewModel.OpenPathAsync("from-home.json");
        Assert.NotEqual("home-imported", fixture.ViewModel.SelectedProfile?.Id);
        fixture.ViewModel.AttachWorkspaceTransfer(fixture.TransferViewModel, null!);
        Assert.Equal("home-imported", fixture.ViewModel.SelectedProfile?.Id);
        Assert.Null(fixture.TransferViewModel.TakePendingProfileActivation());
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Sharing_menu_and_drop_select_the_installed_profile_identically(bool menu) => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        fixture.Transfers.InstalledId = "new-shared-profile";
        fixture.Preferences.Values["new-shared-profile"] = "global-copy";
        if (menu) await fixture.TransferViewModel.OpenCommand.ExecuteAsync(null);
        else await fixture.TransferViewModel.OpenPathAsync("dropped.json");
        Assert.Equal("new-shared-profile", fixture.ViewModel.SelectedProfile?.Id);
        Assert.Equal("global-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        Assert.Equal(1, fixture.Transfers.ManifestImportCalls);
        Assert.Null(fixture.TransferViewModel.TakePendingProfileActivation());
    });

    [Fact]
    public Task Replace_confirmation_explains_the_profile_and_portable_settings_that_will_change() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        fixture.Transfers.HasConflict = true;
        fixture.Dialogs.Responses.Enqueue(ContentDialogResult.Secondary);
        fixture.Dialogs.Responses.Enqueue(ContentDialogResult.Primary);
        await fixture.TransferViewModel.OpenPathAsync("incoming.json");
        Assert.Equal(ProfileImportConflictChoice.Replace, fixture.Transfers.ImportChoice);
        Assert.Equal(2, fixture.Dialogs.Contents.Count);
        Assert.All(fixture.Dialogs.Contents, content =>
        {
            Assert.Contains("sample", content);
            Assert.Contains("출력 포맷", content);
            Assert.Contains("컬럼", content);
            Assert.Contains("DB", content);
        });
    });

    [Fact]
    public Task Metadata_only_package_is_rejected_before_confirmation_or_install_and_disposes_preparation() => OnStaAsync(async () =>
    {
        var datasets = new MetadataOnlyTransfer();
        using var fixture = new ProfileWorkflowFixture(datasets);
        var selected = fixture.ViewModel.SelectedProfile;
        await fixture.TransferViewModel.OpenPathAsync("metadata.fepkg");
        Assert.Equal(0, fixture.Dialogs.Calls);
        Assert.True(datasets.Prepared.Disposed);
        Assert.Equal(0, datasets.ImportCalls);
        Assert.Same(selected, fixture.ViewModel.SelectedProfile);
        Assert.Contains(fixture.Notifications.Messages, text => text.Contains("구조화 결과"));
    });

    private sealed class MetadataOnlyTransfer : IIndexedDatasetTransferService
    {
        public MetadataPreparation Prepared { get; } = new();
        public int ImportCalls { get; private set; }
        public Task<IPreparedWorkspacePackage> PrepareImportAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<IPreparedWorkspacePackage>(Prepared);
        public Task<WorkspacePackageInspection> InspectAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExportAsync(WorkspacePackageExportRequest request, IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImportedWorkspaceSource?> ImportAsync(string path, ProfileImportConflictChoice choice, string? effectiveRoot = null,
            IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        { ImportCalls++; throw new NotSupportedException(); }
        public Task<ImportedWorkspaceSource> RelinkAsync(ImportedWorkspaceSource source, string effectiveRoot,
            IProgress<WorkspaceTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ICatalogProjectionStore OpenCatalogStore(ImportedWorkspaceSource source) => throw new NotSupportedException();
    }

    private sealed class MetadataPreparation : IPreparedWorkspacePackage
    {
        public bool Disposed { get; private set; }
        public WorkspacePackageInspection Inspection { get; } = new(new ProfileManifest { Id = "metadata", DisplayName = "Metadata" },
            @"C:\Archive", @"C:\Archive", null, false, false, 10, 0, false);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
