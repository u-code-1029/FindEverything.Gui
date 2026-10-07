using System.IO;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileAuthoringServiceTests
{
    [Fact]
    public async Task SaveAndApplyAsync_PersistsDeclarativeProfileAndPublishesItImmediately()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(directory.Path, paths);
        var service = provider.GetRequiredService<IProfileAuthoringService>();
        var catalog = provider.GetRequiredService<IProfileCatalog>();
        var changedCount = 0;
        ProfileCatalogChangedEventArgs? changed = null;
        catalog.Changed += (_, eventArgs) =>
        {
            changedCount++;
            changed = eventArgs;
        };

        var result = await service.SaveAndApplyAsync(CreateValidManifest(), originalProfileId: null);

        Assert.True(result.FileSaved);
        Assert.True(result.Applied);
        Assert.True(result.Review.IsValid);
        var snapshot = Assert.IsType<ProfileCatalogSnapshot>(result.Snapshot);
        Assert.Same(snapshot, catalog.Current);
        Assert.Equal(1, changedCount);
        var change = Assert.IsType<ProfileCatalogChangedEventArgs>(changed);
        Assert.Same(ProfileCatalogSnapshot.Empty, change.Previous);
        Assert.Same(snapshot, change.Current);

        var expectedManifestPath = Path.Combine(
            paths.UserProfilesDirectory,
            "gui-sample",
            "profile.json");
        Assert.True(File.Exists(expectedManifestPath));
        var savedJson = await File.ReadAllTextAsync(expectedManifestPath);
        Assert.DoesNotContain("pathInput", savedJson, StringComparison.OrdinalIgnoreCase);

        var profile = Assert.Single(snapshot.Profiles);
        Assert.Equal("gui-sample", profile.Descriptor.Id);
        Assert.Equal("GUI Sample", profile.Descriptor.DisplayName);
        Assert.Equal(ProfileKind.Declarative, profile.Descriptor.Kind);

        var mapping = profile.Map(new ProfilePathCandidate(
            @"C:\Archive\Acme\2026"));
        Assert.Equal(ProfileMapStatus.Success, mapping.Status);
        Assert.NotNull(mapping.Item);
        Assert.Equal("Acme", mapping.Item.Values["client"]);
        Assert.Equal(2026, mapping.Item.Values["year"]);

        var summary = Assert.Single(await service.ListAsync());
        Assert.Equal("gui-sample", summary.Id);
        Assert.Equal(expectedManifestPath, summary.ManifestPath);
        var savedManifest = Assert.IsType<ProfileManifest>(await service.LoadAsync(summary));
        Assert.Equal(ProfileKind.Declarative, savedManifest.Kind);
        Assert.Equal("GUI Sample", savedManifest.DisplayName);

        var samplePath = Path.GetFullPath(Path.Combine(directory.Path, "Beta", "2025"));
        var sample = service.Test(savedManifest, samplePath);
        Assert.True(sample.Review.IsValid);
        Assert.Equal(ProfileMapStatus.Success, sample.Mapping?.Status);
        Assert.Equal("Beta", sample.Mapping?.Item?.Values["client"]);
        Assert.Equal(2025, sample.Mapping?.Item?.Values["year"]);

        savedManifest.DisplayName = "Updated GUI Sample";
        var updated = await service.SaveAndApplyAsync(savedManifest, "gui-sample");

        Assert.True(updated.FileSaved);
        Assert.True(updated.Applied);
        Assert.Equal(2, changedCount);
        Assert.Equal(
            "Updated GUI Sample",
            Assert.Single(catalog.Current.Profiles).Descriptor.DisplayName);
    }

    [Fact]
    public async Task SaveAndApplyAsync_WhenDefinitionIsInvalid_DoesNotWriteOrPublish()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(directory.Path, paths);
        var service = provider.GetRequiredService<IProfileAuthoringService>();
        var catalog = provider.GetRequiredService<IProfileCatalog>();
        var changedCount = 0;
        catalog.Changed += (_, _) => changedCount++;
        var manifest = CreateValidManifest();
        manifest.Fields = [];

        var result = await service.SaveAndApplyAsync(manifest, originalProfileId: null);

        Assert.False(result.FileSaved);
        Assert.False(result.Applied);
        Assert.False(result.Review.IsValid);
        Assert.Null(result.Snapshot);
        Assert.Contains(
            result.Review.Diagnostics,
            static diagnostic => diagnostic.Code == "capture_fields_missing");
        Assert.Same(ProfileCatalogSnapshot.Empty, catalog.Current);
        Assert.Equal(0, changedCount);
        Assert.False(Directory.Exists(paths.UserProfilesDirectory));
    }

    [Fact]
    public async Task SaveAndApplyAsync_WhenManifestChangedExternally_DoesNotOverwriteIt()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(directory.Path, paths);
        var service = provider.GetRequiredService<IProfileAuthoringService>();
        var catalog = provider.GetRequiredService<IProfileCatalog>();
        var created = await service.SaveAndApplyAsync(CreateValidManifest(), originalProfileId: null);
        Assert.True(created.Applied);
        var summary = Assert.Single(await service.ListAsync());
        var manifest = Assert.IsType<ProfileManifest>(await service.LoadAsync(summary));
        await File.AppendAllTextAsync(summary.ManifestPath, Environment.NewLine);
        manifest.DisplayName = "Should Not Win";

        var result = await service.SaveAndApplyAsync(manifest, "gui-sample");

        Assert.False(result.FileSaved);
        Assert.False(result.Applied);
        Assert.Contains(
            result.Review.Diagnostics,
            static diagnostic => diagnostic.Code == "profile_changed_externally");
        Assert.EndsWith(Environment.NewLine, await File.ReadAllTextAsync(summary.ManifestPath));
        Assert.Equal(
            "GUI Sample",
            Assert.Single(catalog.Current.Profiles).Descriptor.DisplayName);
    }

    [Fact]
    public async Task SaveAndApplyAsync_PreservesExistingProfilesAndReports()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(directory.Path, paths);
        var service = provider.GetRequiredService<IProfileAuthoringService>();
        var catalog = provider.GetRequiredService<IProfileCatalog>();
        var publisher = provider.GetRequiredService<IProfileCatalogPublisher>();
        var existingProfile = new StubLoadedProfile();
        var existingReport = new ProfilePluginReport(
            Path.Combine(directory.Path, "BundledProfiles", "bundled"),
            existingProfile.Descriptor.Id,
            existingProfile.Descriptor.DisplayName,
            ProfilePluginStatus.Loaded,
            []);
        publisher.Publish(new ProfileCatalogSnapshot(
            [existingProfile],
            [existingReport],
            DateTimeOffset.UtcNow));

        var result = await service.SaveAndApplyAsync(CreateValidManifest(), originalProfileId: null);

        Assert.True(result.Applied);
        Assert.Equal(2, catalog.Current.Profiles.Count);
        Assert.Contains(catalog.Current.Profiles, profile => ReferenceEquals(profile, existingProfile));
        Assert.Contains(catalog.Current.Reports, report => ReferenceEquals(report, existingReport));
        Assert.Contains(catalog.Current.Profiles, static profile =>
            profile.Descriptor.Id == "gui-sample"
            && profile.Descriptor.Kind == ProfileKind.Declarative);
    }

    [Fact]
    public async Task SaveAndApplyAsync_WhenSerializedManifestIsTooLarge_DoesNotWriteOrPublish()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(directory.Path, paths);
        var service = provider.GetRequiredService<IProfileAuthoringService>();
        var catalog = provider.GetRequiredService<IProfileCatalog>();
        var manifest = CreateValidManifest();
        manifest.Rules![0].Pattern = new string('a', checked((int)ProfileManifestLimits.MaximumLengthBytes));

        var result = await service.SaveAndApplyAsync(manifest, originalProfileId: null);

        Assert.False(result.FileSaved);
        Assert.False(result.Applied);
        Assert.Contains(
            result.Review.Diagnostics,
            static diagnostic => diagnostic.Code == "manifest_too_large");
        Assert.Empty(catalog.Current.Profiles);
        Assert.False(File.Exists(Path.Combine(
            paths.UserProfilesDirectory,
            "gui-sample",
            "profile.json")));
    }

    [Fact]
    public async Task SaveAndApplyAsync_WhenAnotherProcessHoldsTheProfileLock_DoesNotWrite()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(directory.Path, paths);
        var service = provider.GetRequiredService<IProfileAuthoringService>();
        var profileDirectory = Path.Combine(paths.UserProfilesDirectory, "gui-sample");
        Directory.CreateDirectory(profileDirectory);
        await using var heldLock = new FileStream(
            Path.Combine(profileDirectory, ".profile.write.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await service.SaveAndApplyAsync(CreateValidManifest(), originalProfileId: null);

        Assert.False(result.FileSaved);
        Assert.False(result.Applied);
        Assert.Contains(
            result.Review.Diagnostics,
            static diagnostic => diagnostic.Code == "profile_write_locked");
        Assert.False(File.Exists(Path.Combine(profileDirectory, "profile.json")));
    }

    private static ServiceProvider CreateProvider(string root, AppPaths paths)
    {
        var bundledProfilesDirectory = Path.Combine(root, "BundledProfiles");
        Directory.CreateDirectory(bundledProfilesDirectory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProfileRuntime();
        services.Configure<PluginDiscoveryOptions>(options =>
        {
            options.ProfilesDirectory = bundledProfilesDirectory;
            options.UserProfilesDirectory = paths.UserProfilesDirectory;
            options.ContractMajor = ProfileContract.CurrentMajor;
        });
        services.AddSingleton(paths);
        services.AddSingleton<IProfileAuthoringService, ProfileAuthoringService>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static AppPaths CreatePaths(string root) =>
        new(
            Path.Combine(root, "LocalData"),
            Path.Combine(root, "LocalData", "appsettings.user.json"));

    private static ProfileManifest CreateValidManifest() =>
        new()
        {
            ContractVersion = ProfileContract.CurrentMajor,
            Kind = ProfileKind.Declarative,
            Id = "gui-sample",
            Version = "1.0.0",
            DisplayName = "GUI Sample",
            CandidateKind = ProfileCandidateKind.Directory,
            Fields =
            [
                new ProfileFieldManifest
                {
                    FieldId = "client",
                    GroupName = "client",
                    Header = "Client",
                    Order = 10,
                    Required = true,
                    Kind = ProfileFieldValueKind.String,
                },
                new ProfileFieldManifest
                {
                    FieldId = "year",
                    GroupName = "year",
                    Header = "Year",
                    Order = 20,
                    Required = true,
                    Kind = ProfileFieldValueKind.Int32,
                    DisplayFormat = "D4",
                },
            ],
            Rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "client-year",
                    Pattern = @"(?:^|[\\/])(?<client>[^\\/]+)[\\/](?<year>\d{4})$",
                    MatchMode = ProfileRegexMatchMode.Partial,
                    IgnoreCase = false,
                    TimeoutMilliseconds = 100,
                },
            ],
        };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"findeverything-profile-authoring-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A test failure should not be hidden by best-effort fixture cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // A test failure should not be hidden by best-effort fixture cleanup.
            }
        }
    }

    private sealed class StubLoadedProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(
            "bundled-profile",
            "1.0.0",
            "Bundled Profile",
            ProfileCandidateKind.Directory,
            [],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }
}
