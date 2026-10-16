using System.IO;
using FindEverything.Application.Catalog;
using FindEverything.Application.Options;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class WorkspaceTransferServiceTests
{
    [Theory]
    [InlineData("ko-KR", "여러 시작 경로")]
    [InlineData("en-US", "Multi-root")]
    public async Task Multi_root_package_export_is_rejected_before_profile_or_metadata_access(string culture, string expected)
    {
        using var fixture = new Fixture();
        using var service = new IndexedDatasetTransferService(fixture.Paths,
            fixture.Provider.GetRequiredService<ProfileTransferService>(),
            fixture.Provider.GetRequiredService<IProfilePresentationStore>(),
            Options.Create(new LocalizationOptions { CultureName = culture }));
        var roots = new[] { Path.Combine(fixture.Root, "Alpha"), Path.Combine(fixture.Root, "Beta") };
        var scope = new CatalogProjectionScope("multi", "shared-profile", roots[0], "missing-metadata.db", "fingerprint", culture, null)
        { RootPathsIdentity = CatalogProjectionScope.CreateRootPathsIdentity(roots) };
        var snapshot = new CatalogProjectionSnapshot("published", scope,
            new(0, 0, 0, 0, 0, false, TimeSpan.Zero, DateTimeOffset.UtcNow), []);
        var destination = Path.Combine(fixture.Root, "existing.fepkg");
        byte[] existing = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(destination, existing);
        var request = new WorkspacePackageExportRequest(Manifest(), "missing-metadata.db", roots[0], destination)
        { CatalogDatabasePath = "missing-catalog.db", CatalogSnapshot = snapshot };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(request));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
        Assert.Empty(await fixture.Provider.GetRequiredService<IProfileAuthoringService>().ListAsync());
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }
}
