using System.IO;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SettingsReloadDiagnosticsTests
{
    [Fact]
    public async Task Watcher_contains_invalid_json_and_recovers_on_a_valid_locale_edit()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(
            directory.Path,
            Path.Combine(directory.Path, "appsettings.user.json"));
        await File.WriteAllTextAsync(
            paths.UserSettingsFile,
            """
            {
              "Indexing": { "SearchPageSize": 123 },
              "Localization": { "CultureName": "ko-KR" }
            }
            """);

        var initialIndexing = new IndexingOptions { SearchPageSize = 123 };
        var workspace = new RecordingValidatedSettings<WorkspaceOptions>(new());
        var indexing = new RecordingValidatedSettings<IndexingOptions>(initialIndexing);
        var appearance = new RecordingValidatedSettings<AppearanceOptions>(new());
        var localization = new RecordingValidatedSettings<LocalizationOptions>(new());
        var restart = new RecordingRestartCoordinator();
        using var diagnostics = CreateDiagnostics(
            paths,
            workspace,
            indexing,
            appearance,
            localization,
            restart,
            LocalizationOptions.KoreanCultureName,
            []);

        await diagnostics.StartAsync(CancellationToken.None);
        try
        {
            await File.WriteAllTextAsync(paths.UserSettingsFile, "{ invalid json");
            await WaitUntilAsync(() => diagnostics.LastError is not null);
            Assert.Same(initialIndexing, indexing.Current);
            Assert.False(restart.IsRestartRequested);

            await File.WriteAllTextAsync(
                paths.UserSettingsFile,
                """
                {
                  "Indexing": { "SearchPageSize": 234 },
                  "Localization": { "CultureName": "en-US" }
                }
                """);
            await WaitUntilAsync(() => restart.IsRestartRequested);
            Assert.Equal(234, indexing.Current.SearchPageSize);
            Assert.Equal(
                LocalizationOptions.EnglishCultureName,
                localization.Current.CultureName);
            Assert.Null(diagnostics.LastError);
        }
        finally
        {
            await diagnostics.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_json_or_options_keep_the_last_valid_state_and_later_valid_edit_applies()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(
            directory.Path,
            Path.Combine(directory.Path, "appsettings.user.json"));
        await File.WriteAllTextAsync(
            paths.UserSettingsFile,
            """
            {
              "Indexing": { "SearchPageSize": 123 },
              "Localization": { "CultureName": "en-US" }
            }
            """);

        var initialWorkspace = new WorkspaceOptions { RootPath = @"C:\LastValid" };
        var initialIndexing = new IndexingOptions { SearchPageSize = 123 };
        var initialAppearance = new AppearanceOptions { Theme = ThemePreference.Light };
        var initialLocalization = new LocalizationOptions
        {
            CultureName = LocalizationOptions.EnglishCultureName,
        };
        var workspace = new RecordingValidatedSettings<WorkspaceOptions>(initialWorkspace);
        var indexing = new RecordingValidatedSettings<IndexingOptions>(initialIndexing);
        var appearance = new RecordingValidatedSettings<AppearanceOptions>(initialAppearance);
        var localization = new RecordingValidatedSettings<LocalizationOptions>(initialLocalization);
        var restart = new RecordingRestartCoordinator();
        using var diagnostics = CreateDiagnostics(
            paths,
            workspace,
            indexing,
            appearance,
            localization,
            restart,
            LocalizationOptions.EnglishCultureName,
            ["--Localization:CultureName=en-US"]);

        // Capture the persisted-locale baseline through the same hosted-service
        // path, then stop the watcher so each reload below is deterministic.
        await diagnostics.StartAsync(CancellationToken.None);
        await diagnostics.StopAsync(CancellationToken.None);

        await File.WriteAllTextAsync(paths.UserSettingsFile, "{ invalid json");
        Assert.False(await diagnostics.ReloadNowAsync());
        Assert.Same(initialWorkspace, workspace.Current);
        Assert.Same(initialIndexing, indexing.Current);
        Assert.Same(initialAppearance, appearance.Current);
        Assert.Same(initialLocalization, localization.Current);
        Assert.False(restart.IsRestartRequested);
        var reloadError = Assert.IsType<string>(diagnostics.LastError);
        Assert.Contains("JSON syntax", reloadError, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            paths.UserSettingsFile,
            """
            {
              "Indexing": { "SearchPageSize": 0 },
              "Localization": { "CultureName": "ko-KR" }
            }
            """);
        Assert.False(await diagnostics.ReloadNowAsync());
        Assert.Same(initialIndexing, indexing.Current);
        Assert.Same(initialLocalization, localization.Current);
        Assert.False(restart.IsRestartRequested);

        await File.WriteAllTextAsync(
            paths.UserSettingsFile,
            """
            {
              "Indexing": { "SearchPageSize": 222 },
              "Localization": { "CultureName": "ko-KR" }
            }
            """);
        Assert.True(await diagnostics.ReloadNowAsync());
        Assert.Equal(222, indexing.Current.SearchPageSize);
        Assert.Equal(
            LocalizationOptions.KoreanCultureName,
            localization.Current.CultureName);
        await WaitUntilAsync(() => restart.IsRestartRequested);
        Assert.Null(diagnostics.LastError);
    }

    private static SettingsReloadDiagnostics CreateDiagnostics(
        AppPaths paths,
        RecordingValidatedSettings<WorkspaceOptions> workspace,
        RecordingValidatedSettings<IndexingOptions> indexing,
        RecordingValidatedSettings<AppearanceOptions> appearance,
        RecordingValidatedSettings<LocalizationOptions> localization,
        RecordingRestartCoordinator restart,
        string appliedCulture,
        IReadOnlyList<string> arguments) =>
        new(
            paths,
            new ApplicationLaunchContext(
                @"C:\Apps\FindEverything.Gui.exe",
                arguments,
                @"C:\Original Working Directory"),
            workspace,
            indexing,
            appearance,
            localization,
            localization,
            new RecordingAppearanceService(),
            new TestAppLocalizer(appliedCulture),
            restart,
            NullLogger<SettingsReloadDiagnostics>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The settings watcher did not observe the file change.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private sealed class RecordingValidatedSettings<T>(T current) :
        IValidatedSettingsState<T>,
        IValidatedSettingsUpdater<T>
        where T : class
    {
        private T _current = current;

        public T Current => Volatile.Read(ref _current);

        public void Publish(T value) => Volatile.Write(ref _current, value);
    }

    private sealed class RecordingAppearanceService : IAppearanceService
    {
        public void Attach(FluentWindow window)
        {
        }

        public void Apply(FluentWindow window, AppearanceOptions options)
        {
        }

        public void ApplyToAttachedWindow(AppearanceOptions options)
        {
        }
    }

    private sealed class RecordingRestartCoordinator : IApplicationRestartCoordinator
    {
        private int _restartRequested;

        public bool IsRestartRequested => Volatile.Read(ref _restartRequested) != 0;

        public void RequestRestart() => Volatile.Write(ref _restartRequested, 1);

        public void RestartAfterShutdownIfRequested()
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"findeverything-reload-tests-{Guid.NewGuid():N}");
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
