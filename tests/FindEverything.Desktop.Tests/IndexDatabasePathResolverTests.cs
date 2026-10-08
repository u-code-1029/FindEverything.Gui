using System.ComponentModel.DataAnnotations;
using System.IO;
using FindEverything.Application.Options;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class IndexDatabasePathResolverTests
{
    [Fact]
    public void Defaults_keep_file_search_and_each_profile_in_isolated_locations()
    {
        var fixture = CreateResolver(new WorkspaceOptions());

        var fileSearch = fixture.Resolver.ResolveFileSearchDatabasePath();
        var firstProfile = fixture.Resolver.ResolveProfileDatabasePath("sample-projects");
        var sameProfileDifferentCase = fixture.Resolver.ResolveProfileDatabasePath("SAMPLE-PROJECTS");
        var secondProfile = fixture.Resolver.ResolveProfileDatabasePath("another.profile");

        Assert.Equal(Path.GetFullPath(fixture.Paths.FileSearchIndexDatabaseFile), fileSearch);
        Assert.Equal(firstProfile, sameProfileDifferentCase);
        Assert.NotEqual(firstProfile, secondProfile);
        Assert.EndsWith(Path.Combine("db.db"), firstProfile, StringComparison.Ordinal);
        Assert.True(IsWithin(firstProfile, fixture.Paths.ProfileIndexesDirectory));
        Assert.True(IsWithin(secondProfile, fixture.Paths.ProfileIndexesDirectory));
        Assert.False(IsWithin(fileSearch, fixture.Paths.ProfileIndexesDirectory));
    }

    [Fact]
    public void Legacy_shared_path_migrates_to_file_search_but_not_to_profiles()
    {
        var root = CreateTestRoot();
        var legacy = Path.Combine(root, "legacy", "metadata.db");
        var fixture = CreateResolver(
            new WorkspaceOptions { DatabasePath = legacy },
            root);

        Assert.Equal(Path.GetFullPath(legacy), fixture.Resolver.ResolveFileSearchDatabasePath());
        Assert.NotEqual(
            Path.GetFullPath(legacy),
            fixture.Resolver.ResolveProfileDatabasePath("profile-a"));
    }

    [Fact]
    public void New_file_search_override_wins_over_the_legacy_value()
    {
        var root = CreateTestRoot();
        var selected = Path.Combine(root, "selected.db");
        var fixture = CreateResolver(new WorkspaceOptions
        {
            DatabasePath = Path.Combine(root, "legacy.db"),
            FileSearchDatabasePath = selected,
        }, root);

        Assert.Equal(Path.GetFullPath(selected), fixture.Resolver.ResolveFileSearchDatabasePath());
    }

    [Fact]
    public void Profile_override_lookup_is_case_insensitive_and_does_not_affect_other_profiles()
    {
        var root = CreateTestRoot();
        var selected = Path.Combine(root, "selected.db");
        var fixture = CreateResolver(new WorkspaceOptions
        {
            ProfileDatabasePaths = new Dictionary<string, string>
            {
                ["Sample-Projects"] = selected,
            },
        }, root);

        Assert.Equal(
            Path.GetFullPath(selected),
            fixture.Resolver.ResolveProfileDatabasePath("sample-projects"));
        Assert.NotEqual(
            Path.GetFullPath(selected),
            fixture.Resolver.ResolveProfileDatabasePath("other-profile"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("CON")]
    [InlineData("프로필/../../outside")]
    public void Untrusted_profile_ids_cannot_escape_the_profile_index_directory(string profileId)
    {
        var fixture = CreateResolver(new WorkspaceOptions());

        var resolved = fixture.Resolver.ResolveProfileDatabasePath(profileId);

        Assert.True(IsWithin(resolved, fixture.Paths.ProfileIndexesDirectory));
        Assert.Equal("db.db", Path.GetFileName(resolved));
        var profileDirectoryName = Path.GetFileName(Path.GetDirectoryName(resolved))!;
        Assert.DoesNotContain("..", profileDirectoryName, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, profileDirectoryName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, profileDirectoryName);
    }

    [Fact]
    public void Sanitized_name_collisions_still_receive_different_stable_paths()
    {
        var fixture = CreateResolver(new WorkspaceOptions());

        var slash = fixture.Resolver.ResolveProfileDatabasePath("a/b");
        var backslash = fixture.Resolver.ResolveProfileDatabasePath("a\\b");

        Assert.NotEqual(slash, backslash);
        Assert.Equal(
            IndexDatabasePathResolver.CreateProfileDirectoryName("a/b"),
            IndexDatabasePathResolver.CreateProfileDirectoryName("A/B"));
    }

    [Fact]
    public void Workspace_options_reject_case_insensitive_duplicate_profile_keys()
    {
        var options = new WorkspaceOptions
        {
            ProfileDatabasePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["profile-a"] = "a.db",
                ["PROFILE-A"] = "b.db",
            },
        };

        var exception = Assert.Throws<ValidationException>(() => Validator.ValidateObject(
            options,
            new ValidationContext(options),
            validateAllProperties: true));

        Assert.Contains("unique ignoring case", exception.Message, StringComparison.Ordinal);
    }

    private static ResolverFixture CreateResolver(
        WorkspaceOptions options,
        string? root = null)
    {
        root ??= CreateTestRoot();
        var paths = new AppPaths(root, Path.Combine(root, "settings.json"));
        var resolver = new IndexDatabasePathResolver(
            paths,
            new StubSettingsState<WorkspaceOptions>(options));
        return new ResolverFixture(paths, resolver);
    }

    private static string CreateTestRoot() =>
        Path.Combine(Path.GetTempPath(), $"database-resolver-{Guid.NewGuid():N}");

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed record ResolverFixture(
        AppPaths Paths,
        IndexDatabasePathResolver Resolver);

    private sealed class StubSettingsState<T>(T current) : IValidatedSettingsState<T>
        where T : class
    {
        public T Current { get; } = current;
    }
}
