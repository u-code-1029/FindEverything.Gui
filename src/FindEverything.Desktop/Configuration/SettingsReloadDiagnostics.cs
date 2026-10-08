using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public interface ISettingsReloadDiagnostics
{
    string? LastError { get; }

    event EventHandler? Changed;
}

public sealed class SettingsReloadDiagnostics :
    ISettingsReloadDiagnostics,
    IHostedService,
    IDisposable
{
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(150);

    private readonly AppPaths _paths;
    private readonly string _settingsFilePath;
    private readonly ApplicationLaunchContext _launchContext;
    private readonly IValidatedSettingsUpdater<WorkspaceOptions> _workspaceUpdater;
    private readonly IValidatedSettingsUpdater<IndexingOptions> _indexingUpdater;
    private readonly IValidatedSettingsUpdater<AppearanceOptions> _appearanceUpdater;
    private readonly IValidatedSettingsUpdater<LocalizationOptions> _localizationUpdater;
    private readonly IValidatedSettingsState<LocalizationOptions> _localizationState;
    private readonly IAppearanceService _appearanceService;
    private readonly IAppLocalizer _localizer;
    private readonly IApplicationRestartCoordinator _restartCoordinator;
    private readonly ILogger<SettingsReloadDiagnostics> _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _lifetimeSource;
    private CancellationTokenSource? _pendingReload;
    private Task? _pendingReloadTask;
    private PersistedLocale _lastPersistedLocale;
    private bool _hasPersistedLocaleBaseline;
    private string? _lastError;
    private int _disposed;

    public SettingsReloadDiagnostics(
        AppPaths paths,
        ApplicationLaunchContext launchContext,
        IValidatedSettingsUpdater<WorkspaceOptions> workspaceUpdater,
        IValidatedSettingsUpdater<IndexingOptions> indexingUpdater,
        IValidatedSettingsUpdater<AppearanceOptions> appearanceUpdater,
        IValidatedSettingsUpdater<LocalizationOptions> localizationUpdater,
        IValidatedSettingsState<LocalizationOptions> localizationState,
        IAppearanceService appearanceService,
        IAppLocalizer localizer,
        IApplicationRestartCoordinator restartCoordinator,
        ILogger<SettingsReloadDiagnostics> logger)
    {
        _paths = paths;
        _settingsFilePath = Path.GetFullPath(paths.UserSettingsFile);
        _launchContext = launchContext;
        _workspaceUpdater = workspaceUpdater;
        _indexingUpdater = indexingUpdater;
        _appearanceUpdater = appearanceUpdater;
        _localizationUpdater = localizationUpdater;
        _localizationState = localizationState;
        _appearanceService = appearanceService;
        _localizer = localizer;
        _restartCoordinator = restartCoordinator;
        _logger = logger;
    }

    public string? LastError => Volatile.Read(ref _lastError);

    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SettingsReloadDiagnostics));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_paths.LocalDataDirectory);

        var settingsDirectory = Path.GetDirectoryName(_settingsFilePath)
            ?? _paths.LocalDataDirectory;

        var watcher = new FileSystemWatcher(settingsDirectory)
        {
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
        };
        watcher.Changed += OnSettingsFileChanged;
        watcher.Created += OnSettingsFileChanged;
        watcher.Deleted += OnSettingsFileChanged;
        watcher.Renamed += OnSettingsFileRenamed;
        watcher.Error += OnWatcherError;

        lock (_sync)
        {
            if (_lifetimeSource is not null)
            {
                watcher.Dispose();
                throw new InvalidOperationException(
                    "The settings watcher has already been started.");
            }

            _lifetimeSource = new CancellationTokenSource();
            _watcher = watcher;
        }

        watcher.EnableRaisingEvents = true;
        try
        {
            var snapshot = await ReadUserSettingsSnapshotAsync(
                cancellationToken).ConfigureAwait(false);
            var persistedLocale = LoadPersistedLocale(snapshot);
            lock (_sync)
            {
                _lastPersistedLocale = persistedLocale;
                _hasPersistedLocaleBaseline = true;
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? pendingTask;
        FileSystemWatcher? watcher;
        CancellationTokenSource? lifetimeSource;
        lock (_sync)
        {
            watcher = _watcher;
            _watcher = null;
            lifetimeSource = _lifetimeSource;
            _lifetimeSource = null;
            lifetimeSource?.Cancel();
            _pendingReload?.Cancel();
            pendingTask = _pendingReloadTask;
        }

        DisposeWatcher(watcher);
        if (pendingTask is not null)
        {
            try
            {
                await pendingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested
                    || lifetimeSource?.IsCancellationRequested == true)
            {
            }
        }

        lifetimeSource?.Dispose();
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs eventArgs)
    {
        try
        {
            if (IsSettingsFile(eventArgs.FullPath))
            {
                QueueReloadSafely();
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void OnSettingsFileRenamed(object sender, RenamedEventArgs eventArgs)
    {
        try
        {
            if (IsSettingsFile(eventArgs.FullPath)
                || IsSettingsFile(eventArgs.OldFullPath))
            {
                QueueReloadSafely();
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private bool IsSettingsFile(string path) =>
        string.Equals(
            Path.GetFullPath(path),
            _settingsFilePath,
            StringComparison.OrdinalIgnoreCase);

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        ReportFailure(eventArgs.GetException());
        QueueReloadSafely();
    }

    private void QueueReloadSafely()
    {
        try
        {
            CancellationTokenSource source;
            lock (_sync)
            {
                if (_lifetimeSource is null || _lifetimeSource.IsCancellationRequested)
                {
                    return;
                }

                _pendingReload?.Cancel();
                source = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeSource.Token);
                _pendingReload = source;
                _pendingReloadTask = ReloadAfterDelayAsync(source);
            }
        }
        catch (Exception exception)
        {
            // FileSystemWatcher invokes handlers on a thread-pool callback. No
            // invalid file or shutdown race may escape back into that callback.
            ReportFailure(exception);
        }
    }

    private async Task ReloadAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(ReloadDebounce, source.Token).ConfigureAwait(false);
            _ = await ReloadNowAsync(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // ReloadNowAsync is deliberately non-throwing, but retain this boundary
            // so future changes cannot leak through an unobserved watcher task.
            ReportFailure(exception);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pendingReload, source))
                {
                    _pendingReload = null;
                    _pendingReloadTask = null;
                }
            }

            source.Dispose();
        }
    }

    internal async Task<bool> ReloadNowAsync(
        CancellationToken cancellationToken = default)
    {
        var enteredGate = false;
        try
        {
            await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;

            var snapshot = await ReadUserSettingsSnapshotAsync(
                cancellationToken).ConfigureAwait(false);
            var configuration = BuildCandidateConfiguration(snapshot);
            using var configurationScope = configuration as IDisposable;
            var candidate = BindAndValidate(configuration);
            var persistedLocale = LoadPersistedLocale(snapshot);
            var currentSnapshot = await ReadUserSettingsSnapshotAsync(
                cancellationToken).ConfigureAwait(false);
            if (!HasSameRevision(snapshot, currentSnapshot))
            {
                throw new IOException(
                    "The user settings file changed again while its update was being validated.");
            }

            LocalizationOptions localization;
            lock (_sync)
            {
                if (_hasPersistedLocaleBaseline
                    && PersistedLocale.AreEquivalent(
                        _lastPersistedLocale,
                        persistedLocale))
                {
                    // A restart-managed final CLI argument represents the current
                    // UI choice. Preserve it for unrelated file edits. A changed
                    // persisted locale is instead an explicit external language
                    // choice and is allowed to supersede earlier CLI/env values.
                    localization = _localizationState.Current;
                }
                else if (persistedLocale.IsSpecified)
                {
                    localization = new LocalizationOptions
                    {
                        CultureName = persistedLocale.CultureName!,
                    };
                }
                else
                {
                    localization = candidate.Localization;
                }
            }

            _workspaceUpdater.Publish(candidate.Workspace);
            _indexingUpdater.Publish(candidate.Indexing);
            _appearanceUpdater.Publish(candidate.Appearance);
            _localizationUpdater.Publish(localization);

            _appearanceService.ApplyToAttachedWindow(candidate.Appearance);

            lock (_sync)
            {
                _lastPersistedLocale = persistedLocale;
                _hasPersistedLocaleBaseline = true;
            }

            SetError(null);
            if (RequiresRestart(_localizer.Culture.Name, localization.CultureName))
            {
                QueueRestartRequest();
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            return false;
        }
        finally
        {
            if (enteredGate)
            {
                try
                {
                    _reloadGate.Release();
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                }
            }
        }
    }

    private IConfigurationRoot BuildCandidateConfiguration(
        UserSettingsSnapshot userSettings)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        if (userSettings.Exists)
        {
            builder.AddJsonStream(new MemoryStream(
                userSettings.Content!,
                writable: false));
        }

        return builder
            .AddEnvironmentVariables("FINDEVERYTHING_")
            // Capture removes only our prior restart-managed locale pair. Original
            // application arguments still retain their normal highest precedence.
            .AddCommandLine(_launchContext.Arguments.ToArray())
            .Build();
    }

    private static PersistedLocale LoadPersistedLocale(
        UserSettingsSnapshot userSettings)
    {
        if (!userSettings.Exists)
        {
            return default;
        }

        var userConfiguration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(
                userSettings.Content!,
                writable: false))
            .Build();
        using var configurationScope = userConfiguration as IDisposable;
        var cultureName = userConfiguration[
            $"{LocalizationOptions.SectionName}:{nameof(LocalizationOptions.CultureName)}"];
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return default;
        }

        if (!LocalizationOptions.IsSupported(cultureName))
        {
            throw new ValidationException(
                "Localization culture must be ko-KR or en-US.");
        }

        return new PersistedLocale(
            IsSpecified: true,
            LocalizationOptions.Normalize(cultureName));
    }

    private async Task<UserSettingsSnapshot> ReadUserSettingsSnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                _paths.UserSettingsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var content = buffer.ToArray();
            return new UserSettingsSnapshot(
                Exists: true,
                content,
                SHA256.HashData(content));
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
    }

    private static bool HasSameRevision(
        UserSettingsSnapshot expected,
        UserSettingsSnapshot actual)
    {
        if (expected.Exists != actual.Exists)
        {
            return false;
        }

        return !expected.Exists
            || CryptographicOperations.FixedTimeEquals(
                expected.Fingerprint!,
                actual.Fingerprint!);
    }

    private static ValidatedCandidate BindAndValidate(IConfiguration configuration)
    {
        var workspace = Bind<WorkspaceOptions>(configuration, WorkspaceOptions.SectionName);
        var indexing = Bind<IndexingOptions>(configuration, IndexingOptions.SectionName);
        var appearance = Bind<AppearanceOptions>(configuration, AppearanceOptions.SectionName);
        var localization = Bind<LocalizationOptions>(configuration, LocalizationOptions.SectionName);
        var host = Bind<HostLifecycleOptions>(configuration, HostLifecycleOptions.SectionName);
        var plugins = Bind<PluginDiscoveryOptions>(configuration, PluginDiscoveryOptions.SectionName);

        ValidateDataAnnotations(workspace);
        ValidateDataAnnotations(indexing);
        ValidateDataAnnotations(appearance);
        ValidateDataAnnotations(localization);
        ValidateDataAnnotations(host);
        ValidateDataAnnotations(plugins);

        if (!Enum.IsDefined(appearance.Theme) || !Enum.IsDefined(appearance.Backdrop))
        {
            throw new ValidationException(
                "Appearance theme and backdrop values must be supported enum values.");
        }

        if (!LocalizationOptions.IsSupported(localization.CultureName))
        {
            throw new ValidationException(
                "Localization culture must be ko-KR or en-US.");
        }

        localization.CultureName = LocalizationOptions.Normalize(localization.CultureName);
        return new ValidatedCandidate(workspace, indexing, appearance, localization);
    }

    private static T Bind<T>(IConfiguration configuration, string sectionName)
        where T : class, new() =>
        configuration.GetSection(sectionName).Get<T>() ?? new T();

    private static void ValidateDataAnnotations<T>(T value)
        where T : class =>
        Validator.ValidateObject(
            value,
            new ValidationContext(value),
            validateAllProperties: true);

    private void ReportFailure(Exception exception)
    {
        string message;
        try
        {
            var detail = string.Equals(
                LocalizationOptions.Normalize(_localizer.Culture.Name),
                LocalizationOptions.EnglishCultureName,
                StringComparison.OrdinalIgnoreCase)
                    ? "The JSON syntax or setting value is invalid."
                    : "JSON 구문 또는 설정 값이 올바르지 않습니다.";
            message = _localizer.Format(
                "Loc.Settings.Reload.Failed",
                "사용자 설정 변경을 적용하지 못했습니다. 마지막 정상 설정을 유지합니다: {0}",
                detail);
        }
        catch (Exception localizationException)
        {
            System.Diagnostics.Trace.TraceError(
                "Could not localize a settings reload failure: {0}",
                localizationException);
            message = "The user settings change could not be applied. The last valid settings remain active.";
        }

        try
        {
            _logger.LogWarning(exception, "Rejected an invalid user settings reload.");
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Trace.TraceError(
                "Could not log a settings reload failure: {0}",
                loggingException);
        }

        SetError(message);
    }

    private void QueueRestartRequest()
    {
        // RequestRestart synchronously marshals WPF shutdown to the UI thread. It
        // must not run inside _pendingReloadTask because App.OnExit stops the host
        // and waits for that same task. A separate worker removes that wait cycle.
        _ = Task.Run(() =>
        {
            try
            {
                _restartCoordinator.RequestRestart();
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        });
    }

    private void SetError(string? message)
    {
        var previous = Interlocked.Exchange(ref _lastError, message);
        if (string.Equals(previous, message, StringComparison.Ordinal))
        {
            return;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "A settings reload notification handler failed: {0}",
                    exception);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FileSystemWatcher? watcher;
        CancellationTokenSource? lifetimeSource;
        lock (_sync)
        {
            watcher = _watcher;
            _watcher = null;
            lifetimeSource = _lifetimeSource;
            _lifetimeSource = null;
            lifetimeSource?.Cancel();
            _pendingReload?.Cancel();
        }

        DisposeWatcher(watcher);
        lifetimeSource?.Dispose();
        _reloadGate.Dispose();
    }

    private static void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    internal static bool RequiresRestart(
        string appliedCultureName,
        string configuredCultureName) =>
        !string.Equals(
            LocalizationOptions.Normalize(appliedCultureName),
            LocalizationOptions.Normalize(configuredCultureName),
            StringComparison.OrdinalIgnoreCase);

    private sealed record ValidatedCandidate(
        WorkspaceOptions Workspace,
        IndexingOptions Indexing,
        AppearanceOptions Appearance,
        LocalizationOptions Localization);

    private readonly record struct UserSettingsSnapshot(
        bool Exists,
        byte[]? Content,
        byte[]? Fingerprint);

    private readonly record struct PersistedLocale(
        bool IsSpecified,
        string? CultureName)
    {
        public static bool AreEquivalent(PersistedLocale left, PersistedLocale right) =>
            left.IsSpecified == right.IsSpecified
            && string.Equals(
                left.CultureName,
                right.CultureName,
                StringComparison.OrdinalIgnoreCase);
    }
}
