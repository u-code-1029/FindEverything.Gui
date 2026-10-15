using System.IO;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class WorkspaceTransferServiceTests
{
    [Fact]
    public async Task Prepared_package_installs_exactly_the_validated_snapshot_without_reopening_the_source()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(request);
        await using var prepared = await service.PrepareImportAsync(request.DestinationPath);
        var originalName = prepared.Inspection.Profile.DisplayName;
        prepared.Inspection.Profile.DisplayName = "Do not install mutated inspection";
        await File.WriteAllTextAsync(request.DestinationPath, "The sender changed the file while confirmation was open.");

        var installed = (await service.ImportPreparedAsync(prepared, ProfileImportConflictChoice.NewProfile))!;

        Assert.Equal(originalName, installed.DisplayName);
        Assert.Equal(originalName, installed.FrozenProfile.DisplayName);
        Assert.True(installed.HasCatalog);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Paths.LocalDataDirectory, "Imports"), ".stage-*"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportPreparedAsync(prepared, ProfileImportConflictChoice.NewProfile));
    }

    [Fact]
    public async Task Cancelling_confirmation_disposes_prepared_files_without_installing_a_profile_or_data()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(request);
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        var previousCount = (await authoring.ListAsync()).Count;
        var prepared = await service.PrepareImportAsync(request.DestinationPath);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Paths.LocalDataDirectory, "Imports"), ".stage-*"));
        Assert.Null(await service.ImportPreparedAsync(prepared, ProfileImportConflictChoice.Cancel));
        await prepared.DisposeAsync();
        await prepared.DisposeAsync();

        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Paths.LocalDataDirectory, "Imports"), ".stage-*"));
        Assert.Empty(await fixture.Provider.GetRequiredService<IImportedWorkspaceRegistry>().ListAsync());
        Assert.Equal(previousCount, (await authoring.ListAsync()).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportPreparedAsync(prepared, ProfileImportConflictChoice.NewProfile));
    }

    [Fact]
    public async Task Cancellation_after_preparation_leaves_no_owned_stage_when_disposed()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(request);
        var prepared = await service.PrepareImportAsync(request.DestinationPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportPreparedAsync(prepared,
            ProfileImportConflictChoice.NewProfile, cancellationToken: cancellation.Token));
        await prepared.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Paths.LocalDataDirectory, "Imports"), ".stage-*"));
        Assert.Empty(await fixture.Provider.GetRequiredService<IImportedWorkspaceRegistry>().ListAsync());
    }
}
