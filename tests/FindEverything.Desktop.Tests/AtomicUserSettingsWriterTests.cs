using System.IO;
using System.Text.Json;
using FindEverything.Application;
using FindEverything.Application.Options;
using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class AtomicUserSettingsWriterTests
{
    [Fact]
    public async Task Profile_entry_paths_round_trip_and_survive_unrelated_settings_updates()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(paths);
        var writer = provider.GetRequiredService<IUserSettingsWriter>();
        var update = CreateUpdate();
        var firstPaths = new List<string> { @"C:\Projects", @"\\server\share\Archive" };
        update.Workspace!.ProfileEntryPaths = new()
        {
            ["first-profile"] = firstPaths,
            ["second-profile"] = [],
        };

        await writer.SaveAsync(update);
        await writer.SaveAsync(new UserSettingsUpdate(
            Appearance: new AppearanceOptions { Theme = ThemePreference.Light }));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.UserSettingsFile));
        var workspaceJson = document.RootElement.GetProperty("Workspace");
        var roundTrip = workspaceJson.Deserialize<WorkspaceOptions>()!;
        Assert.Equal(firstPaths, roundTrip.ProfileEntryPaths!["first-profile"]);
        Assert.Empty(roundTrip.ProfileEntryPaths["second-profile"]);
        Assert.Equal(@"C:\Data", roundTrip.RootPath);
        Assert.Equal(@"C:\Data\index.db", roundTrip.DatabasePath);
        Assert.Same(firstPaths, update.Workspace.ProfileEntryPaths["first-profile"]);
        Assert.Equal(firstPaths,
            provider.GetRequiredService<IValidatedSettingsState<WorkspaceOptions>>().Current.ProfileEntryPaths!["first-profile"]);

        var configuration = new ConfigurationBuilder().AddJsonFile(paths.UserSettingsFile).Build();
        using var reloaded = new ServiceCollection().AddFindEverythingApplication(configuration).BuildServiceProvider();
        var reloadedWorkspace = reloaded.GetRequiredService<IValidatedSettingsState<WorkspaceOptions>>().Current;
        Assert.Equal(firstPaths, reloadedWorkspace.ProfileEntryPaths!["first-profile"]);
        Assert.Empty(reloadedWorkspace.ProfileEntryPaths["second-profile"]);
    }

    [Fact]
    public async Task Invalid_profile_entry_paths_do_not_replace_or_publish_saved_workspace()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(paths);
        var writer = provider.GetRequiredService<IUserSettingsWriter>();
        await writer.SaveAsync(CreateUpdate());
        var settings = provider.GetRequiredService<IValidatedSettingsState<WorkspaceOptions>>();
        var previous = settings.Current;
        var content = await File.ReadAllTextAsync(paths.UserSettingsFile);

        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            writer.SaveAsync(new UserSettingsUpdate(Workspace: new WorkspaceOptions
            {
                ProfileEntryPaths = new() { ["profile"] = [@"relative\folder"] },
            })));

        Assert.Equal(content, await File.ReadAllTextAsync(paths.UserSettingsFile));
        Assert.Same(previous, settings.Current);
    }

    [Fact]
    public async Task Notification_update_is_validated_persisted_and_published_without_overwriting_other_sections()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(paths);
        var writer = provider.GetRequiredService<IUserSettingsWriter>();
        await writer.SaveAsync(CreateUpdate());
        var notifications = new NotificationOptions { AutoDismissSeconds = 8 };
        await writer.SaveAsync(new UserSettingsUpdate(Notifications: notifications));

        Assert.Same(notifications, provider.GetRequiredService<IValidatedSettingsState<NotificationOptions>>().Current);
        var content = await File.ReadAllTextAsync(paths.UserSettingsFile);
        using var document = JsonDocument.Parse(content);
        Assert.Equal(8, document.RootElement.GetProperty("Notifications").GetProperty("AutoDismissSeconds").GetInt32());
        Assert.Equal(321, document.RootElement.GetProperty("Indexing").GetProperty("SearchPageSize").GetInt32());
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            writer.SaveAsync(new UserSettingsUpdate(Notifications: new NotificationOptions { AutoDismissSeconds = 0 })));
        Assert.Equal(content, await File.ReadAllTextAsync(paths.UserSettingsFile));
        Assert.Same(notifications, provider.GetRequiredService<IValidatedSettingsState<NotificationOptions>>().Current);
    }

    [Fact]
    public async Task Save_preserves_unknown_sections_and_replaces_managed_sections()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        await File.WriteAllTextAsync(
            paths.UserSettingsFile,
            """
            {
              "workspace": { "SelectedProfileId": "old-profile" },
              "Host": { "ShutdownTimeout": "00:00:03" },
              "Plugins": { "ProfilesDirectory": "custom-profiles" },
              "FutureFeature": { "Enabled": true }
            }
            """);

        using var provider = CreateProvider(paths);
        var update = CreateUpdate();

        await provider.GetRequiredService<IUserSettingsWriter>().SaveAsync(update);

        await using var stream = File.OpenRead(paths.UserSettingsFile);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("workspace", out _));
        Assert.Equal("new-profile", root.GetProperty("Workspace").GetProperty("SelectedProfileId").GetString());
        Assert.Equal(321, root.GetProperty("Indexing").GetProperty("SearchPageSize").GetInt32());
        Assert.Equal("Dark", root.GetProperty("Appearance").GetProperty("Theme").GetString());
        Assert.Equal("en-US", root.GetProperty("Localization").GetProperty("CultureName").GetString());
        Assert.Equal("00:00:03", root.GetProperty("Host").GetProperty("ShutdownTimeout").GetString());
        Assert.Equal("custom-profiles", root.GetProperty("Plugins").GetProperty("ProfilesDirectory").GetString());
        Assert.True(root.GetProperty("FutureFeature").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public async Task Save_publishes_new_values_to_validated_settings_before_it_returns()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(paths);
        var workspace = provider.GetRequiredService<IValidatedSettingsState<WorkspaceOptions>>();
        var indexing = provider.GetRequiredService<IValidatedSettingsState<IndexingOptions>>();
        var appearance = provider.GetRequiredService<IValidatedSettingsState<AppearanceOptions>>();
        var localization = provider.GetRequiredService<IValidatedSettingsState<LocalizationOptions>>();
        var update = CreateUpdate();

        await provider.GetRequiredService<IUserSettingsWriter>().SaveAsync(update);

        Assert.Same(update.Workspace, workspace.Current);
        Assert.Same(update.Indexing, indexing.Current);
        Assert.Same(update.Appearance, appearance.Current);
        Assert.Same(update.Localization, localization.Current);
    }

    [Fact]
    public async Task Concurrent_partial_updates_do_not_overwrite_unrelated_sections()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var firstProvider = CreateProvider(paths);
        using var secondProvider = CreateProvider(paths);
        var firstWriter = firstProvider.GetRequiredService<IUserSettingsWriter>();
        var secondWriter = secondProvider.GetRequiredService<IUserSettingsWriter>();
        await firstWriter.SaveAsync(CreateUpdate());

        await Task.WhenAll(
            firstWriter.SaveAsync(new UserSettingsUpdate(
                Workspace: new WorkspaceOptions
                {
                    SelectedProfileId = "concurrent-profile",
                    RootPath = @"D:\Concurrent",
                    DatabasePath = @"D:\Concurrent\index.db",
                })),
            secondWriter.SaveAsync(new UserSettingsUpdate(
                Localization: new LocalizationOptions
                {
                    CultureName = LocalizationOptions.KoreanCultureName,
                })));

        await using var stream = File.OpenRead(paths.UserSettingsFile);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal(
            "concurrent-profile",
            root.GetProperty("Workspace").GetProperty("SelectedProfileId").GetString());
        Assert.Equal(
            321,
            root.GetProperty("Indexing").GetProperty("SearchPageSize").GetInt32());
        Assert.Equal(
            "Dark",
            root.GetProperty("Appearance").GetProperty("Theme").GetString());
        Assert.Equal(
            "ko-KR",
            root.GetProperty("Localization").GetProperty("CultureName").GetString());
    }

    [Fact]
    public async Task Save_waits_for_the_cross_process_settings_lock()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        Directory.CreateDirectory(paths.LocalDataDirectory);
        await using var heldLock = new FileStream(
            paths.UserSettingsFile + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var provider = CreateProvider(paths);

        var saveTask = provider
            .GetRequiredService<IUserSettingsWriter>()
            .SaveAsync(CreateUpdate());
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(saveTask.IsCompleted);

        await heldLock.DisposeAsync();
        await saveTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(paths.UserSettingsFile));
    }

    [Fact]
    public async Task Partial_update_publishes_only_the_changed_settings_state()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        using var provider = CreateProvider(paths);
        var writer = provider.GetRequiredService<IUserSettingsWriter>();
        await writer.SaveAsync(CreateUpdate());
        var workspace = provider.GetRequiredService<IValidatedSettingsState<WorkspaceOptions>>();
        var previousWorkspace = workspace.Current;
        var localization = new LocalizationOptions
        {
            CultureName = LocalizationOptions.KoreanCultureName,
        };

        await writer.SaveAsync(new UserSettingsUpdate(Localization: localization));

        Assert.Same(previousWorkspace, workspace.Current);
        Assert.Same(
            localization,
            provider.GetRequiredService<IValidatedSettingsState<LocalizationOptions>>().Current);
    }

    [Fact]
    public async Task Commit_detects_and_restores_an_external_edit_after_the_revision_check()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        await File.WriteAllTextAsync(
            paths.UserSettingsFile,
            """
            {
              "Workspace": { "SelectedProfileId": "initial" },
              "FutureFeature": { "Revision": "initial" }
            }
            """);
        const string externalRevision =
            """
            {
              "Workspace": { "SelectedProfileId": "external" },
              "FutureFeature": { "Revision": "external" }
            }
            """;

        using var provider = CreateProvider(paths);
        var writer = new AtomicUserSettingsWriter(
            paths,
            provider.GetRequiredService<IValidatedSettingsUpdater<WorkspaceOptions>>(),
            provider.GetRequiredService<IValidatedSettingsUpdater<IndexingOptions>>(),
            provider.GetRequiredService<IValidatedSettingsUpdater<AppearanceOptions>>(),
            provider.GetRequiredService<IValidatedSettingsUpdater<LocalizationOptions>>(),
            new ReplaceFileBeforeCommitHook(externalRevision));

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            writer.SaveAsync(new UserSettingsUpdate(
                Localization: new LocalizationOptions
                {
                    CultureName = LocalizationOptions.EnglishCultureName,
                })));

        Assert.Contains("changed while it was being saved", exception.Message);
        await using var stream = File.OpenRead(paths.UserSettingsFile);
        using var document = await JsonDocument.ParseAsync(stream);
        Assert.Equal(
            "external",
            document.RootElement
                .GetProperty("Workspace")
                .GetProperty("SelectedProfileId")
                .GetString());
        Assert.Equal(
            "external",
            document.RootElement
                .GetProperty("FutureFeature")
                .GetProperty("Revision")
                .GetString());
        Assert.False(document.RootElement.TryGetProperty("Localization", out _));
    }

    private static ServiceProvider CreateProvider(AppPaths paths)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(configuration);
        services.AddSingleton(paths);
        services.AddSingleton<IUserSettingsWriter, AtomicUserSettingsWriter>();
        // This focused fixture intentionally composes only the settings slice.
        // ICatalogService is registered by the application layer, but its engine
        // and profile ports are outside the subject of these tests and are never
        // resolved here.
        return services.BuildServiceProvider();
    }

    private static AppPaths CreatePaths(string directory) =>
        new(directory, Path.Combine(directory, "appsettings.user.json"));

    private static UserSettingsUpdate CreateUpdate() =>
        new(
            new WorkspaceOptions
            {
                SelectedProfileId = "new-profile",
                RootPath = @"C:\Data",
                DatabasePath = @"C:\Data\index.db",
            },
            new IndexingOptions
            {
                SearchPageSize = 321,
                MaxEntriesPerSecond = 654,
                DirectoryDelayMilliseconds = 7,
            },
            new AppearanceOptions
            {
                Theme = ThemePreference.Dark,
                Backdrop = BackdropPreference.None,
            },
            new LocalizationOptions
            {
                CultureName = LocalizationOptions.EnglishCultureName,
            });

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"findeverything-settings-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ReplaceFileBeforeCommitHook(string content) :
        IAtomicUserSettingsCommitHook
    {
        public void BeforeCommit(string userSettingsFile) =>
            File.WriteAllText(userSettingsFile, content);
    }
}
