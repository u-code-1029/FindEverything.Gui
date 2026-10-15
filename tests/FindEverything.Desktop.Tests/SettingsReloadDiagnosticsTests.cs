using System.IO;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SettingsReloadDiagnosticsTests
{
    [Theory]
    [InlineData(LanguageRestartDecision.Later, 0)]
    [InlineData(LanguageRestartDecision.RestartNow, 1)]
    public async Task Watcher_does_not_override_the_in_app_restart_decision_even_before_the_write_returns(
        LanguageRestartDecision decision, int expectedRestarts)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var localization = new RecordingValidatedSettings<LocalizationOptions>(new());
        var restart = new RecordingRestartCoordinator();
        var policy = new LanguageRestartPolicy();
        var activity = new CatalogActivityState { IsFilesScanning = decision == LanguageRestartDecision.Later };
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()), localization, restart,
            LocalizationOptions.KoreanCultureName, [], policy: policy, activity: activity);
        var writer = new CallbackSettingsWriter(async update =>
        {
            await WriteLocaleAsync(paths, update.Localization!.CultureName);
            Assert.True(await diagnostics.ReloadNowAsync());
            await diagnostics.PendingRestartTask;
            Assert.False(restart.IsRestartRequested);
        });
        var languages = new LanguageSelectionService(new TestAppLocalizer(), writer, restart,
            prompt: new FixedLanguagePrompt(decision), languageRestartPolicy: policy, activity: activity);

        Assert.True(await languages.ChangeAsync(LocalizationOptions.EnglishCultureName));
        Assert.Equal(expectedRestarts, restart.RestartCount);
        Assert.Equal(decision == LanguageRestartDecision.Later ? LocalizationOptions.EnglishCultureName : null,
            languages.PendingCultureName);
        // A second watcher event, including unrelated setting edits, must not
        // turn an explicit Later into an automatic restart.
        Assert.True(await diagnostics.ReloadNowAsync());
        activity.IsFilesScanning = false;
        await diagnostics.PendingRestartTask;
        Assert.Equal(expectedRestarts, restart.RestartCount);
    }

    [Fact]
    public async Task A_different_external_locale_edit_clears_in_app_ownership_without_disabling_future_external_restarts()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var policy = new LanguageRestartPolicy();
        var restart = new RecordingRestartCoordinator();
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()), restart,
            LocalizationOptions.KoreanCultureName, [], policy: policy);
        var languages = new LanguageSelectionService(new TestAppLocalizer(),
            new CallbackSettingsWriter(update => WriteLocaleAsync(paths, update.Localization!.CultureName)), restart,
            prompt: new FixedLanguagePrompt(LanguageRestartDecision.Later), languageRestartPolicy: policy);
        Assert.True(await languages.ChangeAsync(LocalizationOptions.EnglishCultureName));
        Assert.True(await diagnostics.ReloadNowAsync());
        await diagnostics.PendingRestartTask;
        Assert.False(restart.IsRestartRequested);

        await WriteLocaleAsync(paths, LocalizationOptions.KoreanCultureName);
        Assert.True(await diagnostics.ReloadNowAsync());
        await diagnostics.PendingRestartTask;
        Assert.False(restart.IsRestartRequested);
        await WriteLocaleAsync(paths, LocalizationOptions.EnglishCultureName);
        Assert.True(await diagnostics.ReloadNowAsync());
        await diagnostics.PendingRestartTask;
        Assert.Equal(1, restart.RestartCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task External_locale_restart_waits_for_catalog_or_file_scan_to_finish(bool catalogScan)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var restart = new RecordingRestartCoordinator();
        var activity = new CatalogActivityState { IsScanning = catalogScan, IsFilesScanning = !catalogScan };
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()), restart,
            LocalizationOptions.KoreanCultureName, [], activity: activity);
        await WriteLocaleAsync(paths, LocalizationOptions.EnglishCultureName);
        Assert.True(await diagnostics.ReloadNowAsync());
        await diagnostics.PendingRestartTask;
        Assert.False(restart.IsRestartRequested);
        activity.IsScanning = false;
        activity.IsFilesScanning = false;
        await diagnostics.PendingRestartTask;
        Assert.Equal(1, restart.RestartCount);
    }

    [Fact]
    public async Task Scan_started_while_draft_consent_is_open_defers_the_final_restart_check()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var restart = new RecordingRestartCoordinator();
        var activity = new CatalogActivityState();
        var drafts = new DeferredDraftProtection();
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()), restart,
            LocalizationOptions.KoreanCultureName, [], activity: activity, drafts: drafts);
        await WriteLocaleAsync(paths, LocalizationOptions.EnglishCultureName);
        Assert.True(await diagnostics.ReloadNowAsync());
        await drafts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activity.IsScanning = true;
        drafts.Consent.SetResult(true);
        await diagnostics.PendingRestartTask;
        Assert.False(restart.IsRestartRequested);
        activity.IsScanning = false;
        await diagnostics.PendingRestartTask;
        Assert.Equal(1, restart.RestartCount);
    }

    [Fact]
    public async Task External_restart_also_waits_for_coordinated_operations_without_a_scan_badge()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var restart = new RecordingRestartCoordinator();
        var operations = new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = operations.RunAsync(ApplicationOperationKind.IndexLoad, _ => release.Task);
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()), restart,
            LocalizationOptions.KoreanCultureName, [], operations: operations);
        try
        {
            await WriteLocaleAsync(paths, LocalizationOptions.EnglishCultureName);
            Assert.True(await diagnostics.ReloadNowAsync());
            await diagnostics.PendingRestartTask;
            Assert.False(restart.IsRestartRequested);
        }
        finally
        {
            release.TrySetResult(true);
            await running;
        }
        await diagnostics.PendingRestartTask;
        Assert.Equal(1, restart.RestartCount);
    }

    [Fact]
    public async Task A_failed_in_app_write_does_not_suppress_a_later_external_locale_change()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var restart = new RecordingRestartCoordinator();
        var policy = new LanguageRestartPolicy();
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()), restart,
            LocalizationOptions.KoreanCultureName, [], policy: policy);
        var languages = new LanguageSelectionService(new TestAppLocalizer(),
            new CallbackSettingsWriter(_ => throw new IOException("write failed")), restart,
            prompt: new FixedLanguagePrompt(LanguageRestartDecision.Later), languageRestartPolicy: policy);
        await Assert.ThrowsAsync<IOException>(() => languages.ChangeAsync(LocalizationOptions.EnglishCultureName));
        Assert.Null(languages.PendingCultureName);
        await WriteLocaleAsync(paths, LocalizationOptions.EnglishCultureName);
        Assert.True(await diagnostics.ReloadNowAsync());
        await diagnostics.PendingRestartTask;
        Assert.Equal(1, restart.RestartCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Superseded_or_disposed_deferred_restart_cannot_run_when_the_scan_finishes(bool dispose)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var restart = new RecordingRestartCoordinator();
        var activity = new CatalogActivityState { IsScanning = true };
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()),
            new RecordingValidatedSettings<IndexingOptions>(new()),
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()), restart,
            LocalizationOptions.KoreanCultureName, [], activity: activity);
        await WriteLocaleAsync(paths, LocalizationOptions.EnglishCultureName);
        Assert.True(await diagnostics.ReloadNowAsync());
        if (dispose) diagnostics.Dispose();
        else
        {
            await WriteLocaleAsync(paths, LocalizationOptions.KoreanCultureName);
            Assert.True(await diagnostics.ReloadNowAsync());
        }
        activity.IsScanning = false;
        await diagnostics.PendingRestartTask;
        Assert.False(restart.IsRestartRequested);
    }

    private static Task WriteLocaleAsync(AppPaths paths, string cultureName) =>
        File.WriteAllTextAsync(paths.UserSettingsFile,
            "{\"Localization\":{\"CultureName\":\"" + cultureName + "\"}}");

    [Fact]
    public async Task Notification_and_batch_settings_reload_atomically_and_invalid_values_preserve_the_previous_settings()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "appsettings.user.json"));
        var indexing = new RecordingValidatedSettings<IndexingOptions>(new());
        var notifications = new RecordingValidatedSettings<NotificationOptions>(new());
        using var diagnostics = CreateDiagnostics(paths,
            new RecordingValidatedSettings<WorkspaceOptions>(new()), indexing,
            new RecordingValidatedSettings<AppearanceOptions>(new()),
            new RecordingValidatedSettings<LocalizationOptions>(new()),
            new RecordingRestartCoordinator(), LocalizationOptions.KoreanCultureName, [], notifications);
        await File.WriteAllTextAsync(paths.UserSettingsFile,
            """{"Indexing":{"UiUpdateBatchSize":25},"Notifications":{"AutoDismissSeconds":9}}""");
        Assert.True(await diagnostics.ReloadNowAsync());
        Assert.Equal(25, indexing.Current.UiUpdateBatchSize);
        Assert.Equal(9, notifications.Current.AutoDismissSeconds);

        await File.WriteAllTextAsync(paths.UserSettingsFile,
            """{"Indexing":{"UiUpdateBatchSize":50},"Notifications":{"AutoDismissSeconds":61}}""");
        Assert.False(await diagnostics.ReloadNowAsync());
        Assert.Equal(25, indexing.Current.UiUpdateBatchSize);
        Assert.Equal(9, notifications.Current.AutoDismissSeconds);
    }

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
        IReadOnlyList<string> arguments,
        RecordingValidatedSettings<NotificationOptions>? notifications = null,
        LanguageRestartPolicy? policy = null,
        CatalogActivityState? activity = null,
        IDraftProtectionService? drafts = null,
        IApplicationOperationCoordinator? operations = null) =>
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
            NullLogger<SettingsReloadDiagnostics>.Instance,
            draftProtection: drafts,
            notificationUpdater: notifications,
            languageRestartPolicy: policy,
            activity: activity,
            operations: operations);

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

        public int RestartCount => Volatile.Read(ref _restartRequested);

        public void RequestRestart() => Interlocked.Increment(ref _restartRequested);

        public void RestartAfterShutdownIfRequested()
        {
        }
    }

    private sealed class FixedLanguagePrompt(LanguageRestartDecision decision) : ILanguageRestartPrompt
    {
        public Task<LanguageRestartDecision> ChooseAsync(string cultureName, bool operationRunning, CancellationToken token) =>
            Task.FromResult(decision);
    }

    private sealed class CallbackSettingsWriter(Func<UserSettingsUpdate, Task> save) : IUserSettingsWriter
    {
        public Task SaveAsync(UserSettingsUpdate update, CancellationToken cancellationToken = default) => save(update);
    }

    private sealed class DeferredDraftProtection : IDraftProtectionService
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Consent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Register(IDraftParticipant participant) { }
        public Task<bool> ConfirmAsync(IDraftParticipant participant) => ConfirmAllAsync();
        public Task<bool> ConfirmAllAsync()
        {
            Entered.TrySetResult(true);
            return Consent.Task;
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
