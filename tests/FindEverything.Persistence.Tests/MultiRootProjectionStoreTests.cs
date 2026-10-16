using System.Text.Json;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class MultiRootProjectionStoreTests
{
    [Fact]
    public async Task Multiple_roots_survive_restart_and_query_as_one_generation()
    {
        using var files = new TestFiles();
        var scope = files.Scope("multi-ab", files.A, files.B);
        string generation;
        using (var store = new SqliteCatalogProjectionStore(files.Database))
            generation = Publish(store, scope, Path.Combine(files.A, "first"), Path.Combine(files.B, "second")).Generation;
        var restoredScope = JsonSerializer.Deserialize<CatalogProjectionScope>(JsonSerializer.Serialize(scope))!;
        using var reopened = new SqliteCatalogProjectionStore(files.Database);
        var snapshot = (await reopened.FindAsync(restoredScope))!;
        Assert.Equal(generation, snapshot.Generation);
        Assert.Equal(scope, snapshot.Scope);
        await using var query = reopened.OpenQuery(snapshot, new());
        Assert.Equal(2, await query.CountAsync());
        Assert.Equal(new[] { Path.Combine(files.A, "first"), Path.Combine(files.B, "second") },
            (await query.ReadRangeAsync(0, 500)).Items.Select(row => row.FullPath));
    }

    [Fact]
    public async Task Distinct_root_sets_with_the_same_primary_root_do_not_retire_each_others_publications()
    {
        using var files = new TestFiles();
        using var store = new SqliteCatalogProjectionStore(files.Database);
        var ab = files.Scope("multi-ab", files.A, files.B);
        var ac = files.Scope("multi-ac", files.A, files.C);
        var single = files.Scope("single-a", files.A);
        var first = Publish(store, ab, Path.Combine(files.B, "b"));
        var second = Publish(store, ac, Path.Combine(files.C, "c"));
        var third = Publish(store, single, Path.Combine(files.A, "a"));
        Assert.Equal(first.Generation, (await store.FindAsync(ab))!.Generation);
        Assert.Equal(second.Generation, (await store.FindAsync(ac))!.Generation);
        Assert.Equal(third.Generation, (await store.FindAsync(single))!.Generation);
        Assert.Null(await store.FindAsync(ab with { RootPathsIdentity = ac.RootPathsIdentity }));

        var replacement = Publish(store, ab with { Key = "new-fingerprint", Fingerprint = "new-fingerprint" }, Path.Combine(files.B, "new-b"));
        Assert.Null(await store.FindAsync(ab)); // Only this exact root set's old fingerprint is superseded.
        Assert.Equal(replacement.Generation, (await store.FindLastPublishedAsync(replacement.Scope))!.Generation);
        Assert.Equal(second.Generation, (await store.FindAsync(ac))!.Generation);
        Assert.Equal(third.Generation, (await store.FindAsync(single))!.Generation);
    }

    [Fact]
    public async Task Last_completed_multi_root_result_remains_available_after_metadata_changes_or_cancelled_staging()
    {
        using var files = new TestFiles();
        using var store = new SqliteCatalogProjectionStore(files.Database);
        var scope = files.Scope("multi-ab", files.A, files.B);
        var completed = Publish(store, scope, Path.Combine(files.A, "old-a"), Path.Combine(files.B, "old-b"));
        var changedMetadata = scope with { IndexScanId = Guid.NewGuid() };
        using (var cancelled = store.Begin(changedMetadata, Fields))
        {
            cancelled.Append(Item(Path.Combine(files.A, "new-a")));
            cancelled.Flush();
            Assert.Null(await store.FindAsync(changedMetadata));
            Assert.Equal(completed.Generation, (await store.FindLastPublishedAsync(changedMetadata))!.Generation);
        }
        using var reopened = new SqliteCatalogProjectionStore(files.Database);
        Assert.Equal(completed.Generation, (await reopened.FindLastPublishedAsync(changedMetadata))!.Generation);
        await using var query = reopened.OpenQuery((await reopened.FindLastPublishedAsync(changedMetadata))!, new());
        Assert.Equal(2, await query.CountAsync());
        Assert.DoesNotContain((await query.ReadRangeAsync(0, 500)).Items, row => row.FullPath.EndsWith("new-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Last_completed_lookup_ignores_only_engine_version_not_profile_culture_database_or_roots()
    {
        using var files = new TestFiles();
        using var store = new SqliteCatalogProjectionStore(files.Database);
        var scope = files.Scope("multi-ab", files.A, files.B);
        Publish(store, scope, Path.Combine(files.A, "a"));
        Assert.NotNull(await store.FindLastPublishedAsync(scope with { IndexScanId = null }));
        Assert.Null(await store.FindLastPublishedAsync(scope with { Fingerprint = "changed" }));
        Assert.Null(await store.FindLastPublishedAsync(scope with { ProfileId = "other" }));
        Assert.Null(await store.FindLastPublishedAsync(scope with { CultureName = "ko-KR" }));
        Assert.Null(await store.FindLastPublishedAsync(scope with { DatabasePath = Path.Combine(files.Root, "other.db") }));
        Assert.Null(await store.FindLastPublishedAsync(scope with { RootPathsIdentity = CatalogProjectionScope.CreateRootPathsIdentity([files.A, files.C]) }));
    }

    [Fact]
    public async Task Multi_root_snapshot_export_is_rejected_before_any_destination_is_created()
    {
        using var files = new TestFiles();
        using var store = new SqliteCatalogProjectionStore(files.Database);
        var snapshot = Publish(store, files.Scope("multi-ab", files.A, files.B), Path.Combine(files.A, "a"), Path.Combine(files.B, "b"));
        var destination = Path.Combine(files.Root, "must-not-create.db");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExportSnapshotAsync(snapshot, destination, false,
            files.A, Path.Combine(files.Root, "linked"), "profile", "metadata.db", "en-US"));
        Assert.Contains("Multi-root", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
        Assert.Throws<InvalidDataException>(() => SqliteCatalogProjectionStore.ValidateTransferredSnapshot(files.Database));
    }

    [Fact]
    public void Invalid_root_identity_is_rejected_before_a_stage_can_be_written()
    {
        using var files = new TestFiles();
        using var store = new SqliteCatalogProjectionStore(files.Database);
        var bad = files.Scope("bad", files.A) with { RootPathsIdentity = "[\"relative\",\"other\"]" };
        Assert.Throws<InvalidDataException>(() => store.Begin(bad, Fields));
    }

    private static readonly ProfileFieldDescriptor[] Fields = [new("name", "name", "Name", 0, false, ProfileFieldValueKind.String, true, null, null)];
    private static CatalogItem Item(string path) => new(path, Path.GetFileName(path), "rule", new object(),
        new Dictionary<string, object?> { ["name"] = Path.GetFileName(path) }, false);
    private static CatalogProjectionSnapshot Publish(SqliteCatalogProjectionStore store, CatalogProjectionScope scope, params string[] paths)
    {
        using var writer = store.Begin(scope, Fields);
        foreach (var path in paths) writer.Append(Item(path));
        return writer.Publish(new(paths.Length, paths.Length, 0, 0, 0, false, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow));
    }
    private sealed class TestFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fe-multi-projection-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "results.db");
        public string A => Path.Combine(Root, "Alpha");
        public string B => Path.Combine(Root, "Beta");
        public string C => Path.Combine(Root, "Gamma");
        public TestFiles() => Directory.CreateDirectory(Root);
        public CatalogProjectionScope Scope(string key, params string[] roots) => new(key, "profile", roots[0], Database,
            "fingerprint", "en-US", Guid.NewGuid()) { RootPathsIdentity = CatalogProjectionScope.CreateRootPathsIdentity(roots) };
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
