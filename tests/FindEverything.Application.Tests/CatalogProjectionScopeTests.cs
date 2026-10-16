using System.Text.Json;
using FindEverything.Application.Catalog;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogProjectionScopeTests
{
    [Fact]
    public void Single_root_keeps_existing_identity_version_and_record_roundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "AlphaRoot");
        var version = Guid.NewGuid();
        var scope = Scope(root);
        Assert.Null(CatalogProjectionScope.CreateRootPathsIdentity([root]));
        Assert.Equal(version, CatalogProjectionScope.CreateIndexVersion([new(root, version)]));
        Assert.Equal([root], scope.EffectiveRootPaths);
        Assert.False(scope.IsMultiRoot);
        scope.ValidateRootPaths();
        var json = JsonSerializer.Serialize(scope);
        Assert.DoesNotContain("EffectiveRootPaths", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMultiRoot", json, StringComparison.Ordinal);
        Assert.Equal(scope, JsonSerializer.Deserialize<CatalogProjectionScope>(json));
    }

    [Fact]
    public void Multi_root_identity_is_ordered_distinct_case_preserving_and_value_equal_after_deserialization()
    {
        var first = Path.Combine(Path.GetTempPath(), "AlphaRoot");
        var second = Path.Combine(Path.GetTempPath(), "BetaRoot");
        var identity = CatalogProjectionScope.CreateRootPathsIdentity([second, first, first.ToUpperInvariant()]);
        Assert.Equal(identity, CatalogProjectionScope.CreateRootPathsIdentity([first, second]));
        var scope = Scope(first) with { RootPathsIdentity = identity };
        Assert.True(scope.IsMultiRoot);
        Assert.Equal([first, second], scope.EffectiveRootPaths);
        scope.ValidateRootPaths();
        var restored = JsonSerializer.Deserialize<CatalogProjectionScope>(JsonSerializer.Serialize(scope))!;
        Assert.Equal(scope, restored);
        Assert.Equal(scope.GetHashCode(), restored.GetHashCode());
        Assert.Equal(scope.EffectiveRootPaths, restored.EffectiveRootPaths);
    }

    [Fact]
    public void Every_root_and_index_version_contributes_without_order_or_casing_dependence()
    {
        var first = Path.Combine(Path.GetTempPath(), "AlphaRoot");
        var second = Path.Combine(Path.GetTempPath(), "BetaRoot");
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var original = CatalogProjectionScope.CreateIndexVersion([new(first, a), new(second, b)]);
        Assert.NotNull(original);
        Assert.Equal(original, CatalogProjectionScope.CreateIndexVersion([new(second.ToUpperInvariant(), b), new(first.ToUpperInvariant(), a)]));
        Assert.NotEqual(original, CatalogProjectionScope.CreateIndexVersion([new(first, a), new(second, Guid.NewGuid())]));
        Assert.NotEqual(original, CatalogProjectionScope.CreateIndexVersion([new(first, a), new(second, null)]));
        Assert.Equal(a, CatalogProjectionScope.CreateIndexVersion([new(first, a)]));
        Assert.Throws<ArgumentException>(() => CatalogProjectionScope.CreateIndexVersion([new(first, a), new(first, b)]));
    }

    [Fact]
    public void Root_identity_rejects_unbounded_relative_noncanonical_and_mismatched_input()
    {
        var first = Path.Combine(Path.GetTempPath(), "AlphaRoot");
        var second = Path.Combine(Path.GetTempPath(), "BetaRoot");
        Assert.Throws<ArgumentException>(() => CatalogProjectionScope.CreateRootPathsIdentity([]));
        Assert.Throws<ArgumentException>(() => CatalogProjectionScope.CreateRootPathsIdentity(["relative-path", first]));
        var excess = Enumerable.Range(0, CatalogProjectionScope.MaximumRootPaths + 1)
            .Select(index => Path.Combine(Path.GetTempPath(), $"root-{index}"));
        Assert.Throws<ArgumentException>(() => CatalogProjectionScope.CreateRootPathsIdentity(excess));
        Assert.Throws<ArgumentException>(() => CatalogProjectionScope.CreateIndexVersion([]));
        Assert.Throws<InvalidDataException>(() => (Scope(first) with
            { RootPathsIdentity = new string('x', CatalogProjectionScope.MaximumRootPathsIdentityLength + 1) }).ValidateRootPaths());
        Assert.Throws<InvalidDataException>(() => (Scope(first) with
            { RootPathsIdentity = JsonSerializer.Serialize(new[] { second, first }) }).ValidateRootPaths());
        Assert.Throws<InvalidDataException>(() => (Scope(first) with { RootPathsIdentity = "[null,\"relative\"]" }).ValidateRootPaths());
        Assert.Throws<ArgumentException>(() => (Scope(second) with
            { RootPathsIdentity = CatalogProjectionScope.CreateRootPathsIdentity([first, second]) }).ValidateRootPaths());
    }

    private static CatalogProjectionScope Scope(string root) => new("scope", "profile", root,
        Path.Combine(Path.GetTempPath(), "index.db"), "fingerprint", "en-US", Guid.NewGuid());
}
