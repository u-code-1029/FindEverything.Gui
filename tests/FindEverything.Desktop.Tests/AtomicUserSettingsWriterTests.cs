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
        var update = CreateUpdate();

        await provider.GetRequiredService<IUserSettingsWriter>().SaveAsync(update);

        Assert.Same(update.Workspace, workspace.Current);
        Assert.Same(update.Indexing, indexing.Current);
        Assert.Same(update.Appearance, appearance.Current);
    }

    private static ServiceProvider CreateProvider(AppPaths paths)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(configuration);
        services.AddSingleton(paths);
        services.AddSingleton<IUserSettingsWriter, AtomicUserSettingsWriter>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
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
}
