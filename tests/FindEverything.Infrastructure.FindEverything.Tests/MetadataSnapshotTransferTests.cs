using FindEverything.Application.Indexing;
using FindEverything.Application.Sharing;
using FindEverything.Infrastructure.FindEverything;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FindEverything.Infrastructure.FindEverything.Tests;

public sealed class MetadataSnapshotTransferTests
{
    [Fact]
    public async Task RootSubsetIsOfflineSearchableUnderNewRootWithoutLeakingOtherRoots()
    {
        using var workspace = new TestWorkspace();
        var first = Path.Combine(workspace.SourcePath, "first");
        var secret = Path.Combine(workspace.SourcePath, "secret");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(secret);
        await File.WriteAllTextAsync(Path.Combine(first, "visible.txt"), "metadata");
        await File.WriteAllTextAsync(Path.Combine(secret, "OTHER-ROOT-SECRET.txt"), "private");
        var factory = new FindEverythingIndexSessionFactory();
        await using (var session = factory.Create(workspace.DatabasePath))
        {
            await session.ScanAsync(FastScan(first));
            await session.ScanAsync(FastScan(secret));
        }
        var linked = Path.Combine(workspace.SourcePath, "missing-receiver-root");
        var exported = Path.Combine(Path.GetDirectoryName(workspace.DatabasePath)!, "shared.db");
        var info = await MetadataSnapshotTransfer.CopyAsync(workspace.DatabasePath, first, exported, linked);
        Assert.Equal(linked, info.RootPath);
        Assert.Equal(1, info.EntryCount);
        Assert.False(Directory.Exists(linked));
        await using var reader = factory.Create(exported);
        var result = await reader.SearchEntriesAsync(new(linked) { Keyword = "visible", Limit = 20 });
        Assert.Equal(Path.Combine(linked, "visible.txt"), Assert.Single(result.Entries).FullPath);
        Assert.Empty((await reader.SearchEntriesAsync(new(linked) { Keyword = "SECRET", Limit = 20 })).Entries);
        var inspected = MetadataSnapshotTransfer.Inspect(exported, linked, requireSingleRoot: true);
        Assert.Equal(info.ScanId, inspected.ScanId);
        using var connection = new SqliteConnection($"Data Source={exported};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM roots;";
        Assert.Equal(1L, command.ExecuteScalar());
        foreach (var table in new[] { "scans", "staging", "staged_costs", "staged_pending" })
        {
            command.CommandText = "SELECT COUNT(*) FROM " + table;
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    [Fact]
    public async Task RootLinkPreservesPendingCoverageAndCosts()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, "deferred"));
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, "ordinary"));
        var factory = new FindEverythingIndexSessionFactory();
        await using (var session = factory.Create(workspace.DatabasePath))
            await session.ScanAsync(FastScan(workspace.SourcePath) with
            { Options = FastScan(workspace.SourcePath).Options with { Deferral = new IndexDeferralPolicy { DirectoryNames = ["deferred"] } } });
        var linked = Path.Combine(Path.GetDirectoryName(workspace.SourcePath)!, "linked");
        var exported = Path.Combine(Path.GetDirectoryName(workspace.DatabasePath)!, "deferred.db");
        var snapshot = await MetadataSnapshotTransfer.CopyAsync(workspace.DatabasePath, workspace.SourcePath, exported, linked);
        Assert.True(snapshot.HasPendingScopes);
        await using var read = factory.Create(exported);
        var result = await read.SearchEntriesAsync(new(linked));
        Assert.True(result.HasPendingScopes);
        Assert.Contains(result.Entries, row => row.Name == "deferred" && row.CoveragePending);
    }

    [Fact]
    public async Task TransferRejectsAlienDatabaseWithoutReplacingDestination()
    {
        using var workspace = new TestWorkspace();
        var alien = Path.Combine(workspace.SourcePath, "alien.db");
        using (var connection = new SqliteConnection($"Data Source={alien};Pooling=false"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE unrelated(value TEXT);"; command.ExecuteNonQuery(); }
        var target = Path.Combine(workspace.SourcePath, "target.db");
        await Assert.ThrowsAsync<InvalidDataException>(() => MetadataSnapshotTransfer.CopyAsync(alien, workspace.SourcePath, target, workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    private static IndexScanRequest FastScan(string path) => new(path)
    { Options = new IndexScanOptions { DirectoryDelay = TimeSpan.Zero, MaxEntriesPerSecond = 100_000, BatchSize = 16 } };
}
