using System.IO;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Services;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class WorkspaceTransferServiceTests
{
    [Fact]
    public async Task Retired_generation_survives_store_and_independent_query_leases_then_is_reclaimed()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IndexedDatasetTransferService>();
        await service.ExportAsync(request);
        var source = (await service.ImportAsync(request.DestinationPath, ProfileImportConflictChoice.NewProfile))!;
        var previous = Path.GetDirectoryName(source.MetadataDatabasePath)!;
        var store = (IImportedCatalogProjectionStore)service.OpenCatalogStore(source);
        var query = store.OpenImportedQuery(new());
        try
        {
            var updated = await service.RelinkAsync(source, Path.Combine(fixture.Root, "new-root"));
            await service.CleanupRetiredGenerationsAsync(updated);
            Assert.True(Directory.Exists(previous));
            store.Dispose();
            await service.CleanupRetiredGenerationsAsync(updated);
            Assert.True(Directory.Exists(previous)); // A lazy query can outlive its owner.
            await query.DisposeAsync();
            await service.CleanupRetiredGenerationsAsync(updated);
            Assert.False(Directory.Exists(previous));
            Assert.True(File.Exists(updated.MetadataDatabasePath));
            Assert.True(File.Exists(updated.CatalogDatabasePath));
            Assert.True(File.Exists(Path.Combine(updated.DirectoryPath, "archive", "metadata.db")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RelinkAsync(source, request.RootPath));
        }
        finally { await query.DisposeAsync(); store.Dispose(); }
    }

    [Fact]
    public async Task Legacy_generations_without_a_reader_marker_are_retained_conservatively()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IndexedDatasetTransferService>();
        await service.ExportAsync(request);
        var source = (await service.ImportAsync(request.DestinationPath, ProfileImportConflictChoice.NewProfile))!;
        var previous = Path.GetDirectoryName(source.MetadataDatabasePath)!;
        File.Delete(Path.Combine(previous, ImportedGenerationLease.MarkerFileName));
        var updated = await service.RelinkAsync(source, Path.Combine(fixture.Root, "new-root"));
        await service.CleanupRetiredGenerationsAsync(updated);
        Assert.True(File.Exists(source.MetadataDatabasePath));
        Assert.True(File.Exists(updated.MetadataDatabasePath));
    }

    [Fact]
    public async Task Shared_reader_file_handle_prevents_cleanup_until_released()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IndexedDatasetTransferService>();
        await service.ExportAsync(request);
        var source = (await service.ImportAsync(request.DestinationPath, ProfileImportConflictChoice.NewProfile))!;
        var previous = Path.GetDirectoryName(source.MetadataDatabasePath)!;
        var handle = new FileStream(Path.Combine(previous, ImportedGenerationLease.MarkerFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var updated = await service.RelinkAsync(source, Path.Combine(fixture.Root, "new-root"));
            await service.CleanupRetiredGenerationsAsync(updated);
            Assert.True(Directory.Exists(previous));
            handle.Dispose();
            await service.CleanupRetiredGenerationsAsync(updated);
            Assert.False(Directory.Exists(previous));
            Assert.True(Directory.Exists(Path.GetDirectoryName(updated.MetadataDatabasePath)!));
        }
        finally { handle.Dispose(); }
    }
}
