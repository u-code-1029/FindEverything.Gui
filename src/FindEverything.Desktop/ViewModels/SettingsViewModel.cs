using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsWriter _settingsWriter;
    private readonly IAppearanceService _appearanceService;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly ISettingsReloadDiagnostics _reloadDiagnostics;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _isShowingReloadError;

    public SettingsViewModel(
        IValidatedSettingsState<IndexingOptions> indexingSettings,
        IValidatedSettingsState<AppearanceOptions> appearanceSettings,
        IUserSettingsWriter settingsWriter,
        IAppearanceService appearanceService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        ISettingsReloadDiagnostics reloadDiagnostics,
        IAppLocalizer localizer,
        AppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settingsWriter = settingsWriter;
        _appearanceService = appearanceService;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _reloadDiagnostics = reloadDiagnostics;
        _localizer = localizer;
        _logger = logger;

        ThemeChoices =
        [
            new(ThemePreference.System, L("Loc.Settings.Theme.System", "시스템 설정 사용")),
            new(ThemePreference.Light, L("Loc.Settings.Theme.Light", "라이트")),
            new(ThemePreference.Dark, L("Loc.Settings.Theme.Dark", "다크")),
        ];
        BackdropChoices =
        [
            new(BackdropPreference.Auto, L("Loc.Settings.Backdrop.Auto", "자동 (지원 시 효과 사용)")),
            new(BackdropPreference.None, L("Loc.Settings.Backdrop.None", "사용 안 함")),
        ];

        var indexing = indexingSettings.Current;
        SearchPageSize = indexing.SearchPageSize;
        MaxEntriesPerSecond = indexing.MaxEntriesPerSecond;
        DirectoryDelayMilliseconds = indexing.DirectoryDelayMilliseconds;

        var appearance = appearanceSettings.Current;
        SelectedTheme = ThemeChoices.First(choice => choice.Value == appearance.Theme);
        SelectedBackdrop = BackdropChoices.First(choice => choice.Value == appearance.Backdrop);
        UserSettingsFile = paths.UserSettingsFile;
        StatusMessage = L(
            "Loc.Settings.Status.Initial",
            "변경 사항은 사용자 설정 파일에 저장됩니다.");

        _reloadDiagnostics.Changed += OnReloadDiagnosticsChanged;
        ApplyReloadDiagnostics();
    }

    public IReadOnlyList<SettingChoice<ThemePreference>> ThemeChoices { get; }

    public IReadOnlyList<SettingChoice<BackdropPreference>> BackdropChoices { get; }

    public string UserSettingsFile { get; }

    [ObservableProperty]
    private int _searchPageSize;

    [ObservableProperty]
    private int _maxEntriesPerSecond;

    [ObservableProperty]
    private int _directoryDelayMilliseconds;

    [ObservableProperty]
    private SettingChoice<ThemePreference> _selectedTheme = null!;

    [ObservableProperty]
    private SettingChoice<BackdropPreference> _selectedBackdrop = null!;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _statusMessage = "변경 사항은 사용자 설정 파일에 저장됩니다.";

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    partial void OnIsSavingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private Task SaveAsync() => SaveCoreAsync(showResetMessage: false);

    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private async Task ResetAsync()
    {
        var result = await _contentDialogService.ShowAsync(
            new ContentDialog
            {
                Title = L("Loc.Settings.Reset.Title", "설정을 초기화할까요?"),
                Content = L(
                    "Loc.Settings.Reset.Message",
                    "인덱싱과 화면 설정을 기본값으로 되돌립니다. 작업 위치 설정은 유지됩니다."),
                PrimaryButtonText = L("Loc.Settings.Reset.Action", "초기화"),
                CloseButtonText = L("Loc.Common.Cancel", "취소"),
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonAppearance = ControlAppearance.Danger,
            },
            CancellationToken.None).ConfigureAwait(true);

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var indexing = new IndexingOptions();
        SearchPageSize = indexing.SearchPageSize;
        MaxEntriesPerSecond = indexing.MaxEntriesPerSecond;
        DirectoryDelayMilliseconds = indexing.DirectoryDelayMilliseconds;
        SelectedTheme = ThemeChoices.First(choice => choice.Value == ThemePreference.System);
        SelectedBackdrop = BackdropChoices.First(choice => choice.Value == BackdropPreference.Auto);
        await SaveCoreAsync(showResetMessage: true).ConfigureAwait(true);
    }

    private bool CanChangeSettings() => !IsSaving;

    private void OnReloadDiagnosticsChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyReloadDiagnostics();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(ApplyReloadDiagnostics);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogDebug(exception, "Ignored a settings reload notification during dispatcher shutdown.");
        }
    }

    private void ApplyReloadDiagnostics()
    {
        var error = _reloadDiagnostics.LastError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            _isShowingReloadError = true;
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = error;
            return;
        }

        if (_isShowingReloadError)
        {
            _isShowingReloadError = false;
            StatusSeverity = InfoBarSeverity.Informational;
            StatusMessage = L(
                "Loc.Settings.Status.Reloaded",
                "사용자 설정 파일 변경을 적용했습니다.");
        }
    }

    private async Task SaveCoreAsync(bool showResetMessage)
    {
        IsSaving = true;
        try
        {
            var appearance = new AppearanceOptions
            {
                Theme = SelectedTheme.Value,
                Backdrop = SelectedBackdrop.Value,
            };
            await _settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Indexing: new IndexingOptions
                    {
                        SearchPageSize = SearchPageSize,
                        MaxEntriesPerSecond = MaxEntriesPerSecond,
                        DirectoryDelayMilliseconds = DirectoryDelayMilliseconds,
                    },
                    Appearance: appearance)).ConfigureAwait(true);

            if (System.Windows.Application.Current.MainWindow is FluentWindow window)
            {
                _appearanceService.Apply(window, appearance);
            }

            StatusSeverity = InfoBarSeverity.Success;
            StatusMessage = showResetMessage
                ? L("Loc.Settings.Status.Reset", "기본 설정으로 초기화했습니다.")
                : L("Loc.Settings.Status.Saved", "설정을 저장했습니다.");
            _snackbarService.Show(
                showResetMessage
                    ? L("Loc.Settings.Reset.Completed", "설정 초기화 완료")
                    : L("Loc.Settings.Save.Completed", "설정 저장 완료"),
                StatusMessage,
                ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not save user settings.");
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = L(
                "Loc.Settings.Save.Failed.Message",
                "설정을 저장하지 못했습니다. 입력값과 사용자 설정 파일 권한을 확인하세요.");
            _snackbarService.Show(
                L("Loc.Settings.Save.Failed", "설정 저장 실패"),
                StatusMessage,
                ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);
}

public sealed record SettingChoice<T>(T Value, string DisplayName)
    where T : struct, Enum;
