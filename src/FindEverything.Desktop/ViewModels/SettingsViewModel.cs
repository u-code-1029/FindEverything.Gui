using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using System.Globalization;
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
    private readonly IValidatedSettingsState<IndexingOptions> _indexingSettings;
    private readonly IValidatedSettingsState<AppearanceOptions> _appearanceSettings;
    private readonly IValidatedSettingsState<NotificationOptions> _notificationSettings;
    private readonly IUiScheduler _uiScheduler;
    private bool _isRefreshing;
    private string _savedSearchPageSize = string.Empty;
    private string _savedMaxEntriesPerSecond = string.Empty;
    private string _savedDirectoryDelay = string.Empty;
    private string _savedUiUpdateBatchSize = string.Empty;
    private string _savedNotificationAutoDismissSeconds = string.Empty;
    private ThemePreference _savedTheme;
    private BackdropPreference _savedBackdrop;
    private CatalogRowDensity _savedCatalogRowDensity;

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
        ILogger<SettingsViewModel> logger,
        IUiScheduler? uiScheduler = null,
        IValidatedSettingsState<NotificationOptions>? notificationSettings = null)
    {
        _settingsWriter = settingsWriter;
        _appearanceService = appearanceService;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _reloadDiagnostics = reloadDiagnostics;
        _localizer = localizer;
        _logger = logger;
        _indexingSettings = indexingSettings;
        _appearanceSettings = appearanceSettings;
        _notificationSettings = notificationSettings ?? DefaultNotificationSettings.Instance;
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;

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
        CatalogRowDensityChoices =
        [
            new(CatalogRowDensity.Narrow, L("Loc.Settings.Density.Narrow", "좁게")),
            new(CatalogRowDensity.Normal, L("Loc.Settings.Density.Normal", "보통")),
            new(CatalogRowDensity.Wide, L("Loc.Settings.Density.Wide", "넓게")),
        ];

        var indexing = indexingSettings.Current;
        SearchPageSize = indexing.SearchPageSize;
        MaxEntriesPerSecond = indexing.MaxEntriesPerSecond;
        DirectoryDelayMilliseconds = indexing.DirectoryDelayMilliseconds;
        UiUpdateBatchSize = indexing.UiUpdateBatchSize;
        NotificationAutoDismissSeconds = _notificationSettings.Current.AutoDismissSeconds;

        var appearance = appearanceSettings.Current;
        SelectedTheme = ThemeChoices.First(choice => choice.Value == appearance.Theme);
        SelectedBackdrop = BackdropChoices.First(choice => choice.Value == appearance.Backdrop);
        SelectedCatalogRowDensity = CatalogRowDensityChoices.First(choice => choice.Value == appearance.CatalogRowDensity);
        RefreshFromSavedSettings(force: true);
        UserSettingsFile = paths.UserSettingsFile;
        StatusMessage = L(
            "Loc.Settings.Status.Initial",
            "변경 사항은 사용자 설정 파일에 저장됩니다.");

        _reloadDiagnostics.Changed += OnReloadDiagnosticsChanged;
        ApplyReloadDiagnostics();
    }

    public IReadOnlyList<SettingChoice<ThemePreference>> ThemeChoices { get; }

    public IReadOnlyList<SettingChoice<BackdropPreference>> BackdropChoices { get; }

    public IReadOnlyList<SettingChoice<CatalogRowDensity>> CatalogRowDensityChoices { get; }

    public string UserSettingsFile { get; }

    [ObservableProperty]
    private int _searchPageSize;

    [ObservableProperty]
    private int _maxEntriesPerSecond;

    [ObservableProperty]
    private int _directoryDelayMilliseconds;

    [ObservableProperty]
    private int _uiUpdateBatchSize;

    [ObservableProperty]
    private int _notificationAutoDismissSeconds;

    [ObservableProperty]
    private string _searchPageSizeText = string.Empty;

    [ObservableProperty]
    private string _maxEntriesPerSecondText = string.Empty;

    [ObservableProperty]
    private string _directoryDelayMillisecondsText = string.Empty;

    [ObservableProperty]
    private string _uiUpdateBatchSizeText = string.Empty;

    [ObservableProperty]
    private string _notificationAutoDismissSecondsText = string.Empty;

    [ObservableProperty]
    private string _numericValidationMessage = string.Empty;

    public bool HasNumericValidationError => NumericValidationMessage.Length > 0;

    public bool IsEditable => !IsSaving;

    public bool IsSaveAvailable => IsEditable && !HasNumericValidationError;

    partial void OnSearchPageSizeTextChanged(string value) => ValidateNumericDrafts();
    partial void OnMaxEntriesPerSecondTextChanged(string value) => ValidateNumericDrafts();
    partial void OnDirectoryDelayMillisecondsTextChanged(string value) => ValidateNumericDrafts();
    partial void OnUiUpdateBatchSizeTextChanged(string value) => ValidateNumericDrafts();
    partial void OnNotificationAutoDismissSecondsTextChanged(string value) => ValidateNumericDrafts();

    partial void OnSearchPageSizeChanged(int value)
    {
        if (!_isRefreshing) SearchPageSizeText = value.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnMaxEntriesPerSecondChanged(int value)
    {
        if (!_isRefreshing) MaxEntriesPerSecondText = value.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnDirectoryDelayMillisecondsChanged(int value)
    {
        if (!_isRefreshing) DirectoryDelayMillisecondsText = value.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnUiUpdateBatchSizeChanged(int value)
    {
        if (!_isRefreshing) UiUpdateBatchSizeText = value.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnNotificationAutoDismissSecondsChanged(int value)
    {
        if (!_isRefreshing) NotificationAutoDismissSecondsText = value.ToString(CultureInfo.InvariantCulture);
    }

    [ObservableProperty]
    private SettingChoice<ThemePreference> _selectedTheme = null!;

    [ObservableProperty]
    private SettingChoice<BackdropPreference> _selectedBackdrop = null!;

    [ObservableProperty]
    private SettingChoice<CatalogRowDensity> _selectedCatalogRowDensity = null!;

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
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsSaveAvailable));
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
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
        UiUpdateBatchSize = indexing.UiUpdateBatchSize;
        NotificationAutoDismissSeconds = new NotificationOptions().AutoDismissSeconds;
        SearchPageSizeText = indexing.SearchPageSize.ToString(CultureInfo.InvariantCulture);
        MaxEntriesPerSecondText = indexing.MaxEntriesPerSecond.ToString(CultureInfo.InvariantCulture);
        DirectoryDelayMillisecondsText = indexing.DirectoryDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        UiUpdateBatchSizeText = indexing.UiUpdateBatchSize.ToString(CultureInfo.InvariantCulture);
        NotificationAutoDismissSecondsText = NotificationAutoDismissSeconds.ToString(CultureInfo.InvariantCulture);
        SelectedTheme = ThemeChoices.First(choice => choice.Value == ThemePreference.System);
        SelectedBackdrop = BackdropChoices.First(choice => choice.Value == BackdropPreference.Auto);
        SelectedCatalogRowDensity = CatalogRowDensityChoices.First(choice => choice.Value == CatalogRowDensity.Normal);
        await SaveCoreAsync(showResetMessage: true).ConfigureAwait(true);
    }

    private bool CanChangeSettings() => !IsSaving;

    private bool CanSaveSettings() => IsSaveAvailable;

    private void OnReloadDiagnosticsChanged(object? sender, EventArgs e)
    {
        _uiScheduler.Post(ApplyReloadDiagnostics);
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

        if (!IsSaving) RefreshFromSavedSettings(force: false);
    }

    private async Task SaveCoreAsync(bool showResetMessage)
    {
        if (!TryReadNumericDrafts(out var indexing, out var notifications))
        {
            ValidateNumericDrafts();
            StatusSeverity = InfoBarSeverity.Error;
            StatusMessage = NumericValidationMessage;
            return;
        }

        IsSaving = true;
        try
        {
            var appearance = new AppearanceOptions
            {
                Theme = SelectedTheme.Value,
                Backdrop = SelectedBackdrop.Value,
                CatalogRowDensity = SelectedCatalogRowDensity.Value,
            };
            await _settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Indexing: indexing,
                    Appearance: appearance,
                    Notifications: notifications)).ConfigureAwait(true);

            _appearanceService.ApplyToAttachedWindow(appearance);

            _savedSearchPageSize = indexing.SearchPageSize.ToString(CultureInfo.InvariantCulture);
            _savedMaxEntriesPerSecond = indexing.MaxEntriesPerSecond.ToString(CultureInfo.InvariantCulture);
            _savedDirectoryDelay = indexing.DirectoryDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            _savedUiUpdateBatchSize = indexing.UiUpdateBatchSize.ToString(CultureInfo.InvariantCulture);
            _savedNotificationAutoDismissSeconds = notifications.AutoDismissSeconds.ToString(CultureInfo.InvariantCulture);
            _savedTheme = appearance.Theme;
            _savedBackdrop = appearance.Backdrop;
            _savedCatalogRowDensity = appearance.CatalogRowDensity;

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

    private bool TryReadNumericDrafts(out IndexingOptions indexing, out NotificationOptions notifications)
    {
        indexing = new IndexingOptions();
        notifications = new NotificationOptions();
        if (!int.TryParse(SearchPageSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pageSize)
            || pageSize is < 1 or > 1000
            || !int.TryParse(MaxEntriesPerSecondText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxEntries)
            || maxEntries < 1
            || !int.TryParse(DirectoryDelayMillisecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay)
            || delay < 0
            || !int.TryParse(UiUpdateBatchSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchSize)
            || batchSize is < 1 or > 1000
            || !int.TryParse(NotificationAutoDismissSecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dismissSeconds)
            || dismissSeconds is < 1 or > 60) return false;

        indexing.SearchPageSize = pageSize;
        indexing.MaxEntriesPerSecond = maxEntries;
        indexing.DirectoryDelayMilliseconds = delay;
        indexing.UiUpdateBatchSize = batchSize;
        notifications.AutoDismissSeconds = dismissSeconds;
        return true;
    }

    private void ValidateNumericDrafts()
    {
        NumericValidationMessage = TryReadNumericDrafts(out _, out _)
            ? string.Empty
            : L("Loc.Settings.Validation.Numeric", "검색 페이지 크기와 화면 갱신 묶음은 1–1,000, 초당 최대 항목 수는 1 이상, 폴더 지연은 0 이상, 알림 표시 시간은 1–60초인 정수를 입력하세요.");
        OnPropertyChanged(nameof(HasNumericValidationError));
        OnPropertyChanged(nameof(IsSaveAvailable));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RefreshFromSavedSettings(bool force)
    {
        var indexing = _indexingSettings.Current;
        var appearance = _appearanceSettings.Current;
        var notifications = _notificationSettings.Current;
        _isRefreshing = true;
        try
        {
            if (force || SearchPageSizeText == _savedSearchPageSize)
            {
                SearchPageSize = indexing.SearchPageSize;
                SearchPageSizeText = indexing.SearchPageSize.ToString(CultureInfo.InvariantCulture);
            }
            if (force || MaxEntriesPerSecondText == _savedMaxEntriesPerSecond)
            {
                MaxEntriesPerSecond = indexing.MaxEntriesPerSecond;
                MaxEntriesPerSecondText = indexing.MaxEntriesPerSecond.ToString(CultureInfo.InvariantCulture);
            }
            if (force || DirectoryDelayMillisecondsText == _savedDirectoryDelay)
            {
                DirectoryDelayMilliseconds = indexing.DirectoryDelayMilliseconds;
                DirectoryDelayMillisecondsText = indexing.DirectoryDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            }
            if (force || UiUpdateBatchSizeText == _savedUiUpdateBatchSize)
            {
                UiUpdateBatchSize = indexing.UiUpdateBatchSize;
                UiUpdateBatchSizeText = indexing.UiUpdateBatchSize.ToString(CultureInfo.InvariantCulture);
            }
            if (force || NotificationAutoDismissSecondsText == _savedNotificationAutoDismissSeconds)
            {
                NotificationAutoDismissSeconds = notifications.AutoDismissSeconds;
                NotificationAutoDismissSecondsText = notifications.AutoDismissSeconds.ToString(CultureInfo.InvariantCulture);
            }
            if (force || SelectedTheme.Value == _savedTheme)
                SelectedTheme = ThemeChoices.First(choice => choice.Value == appearance.Theme);
            if (force || SelectedBackdrop.Value == _savedBackdrop)
                SelectedBackdrop = BackdropChoices.First(choice => choice.Value == appearance.Backdrop);
            if (force || SelectedCatalogRowDensity.Value == _savedCatalogRowDensity)
                SelectedCatalogRowDensity = CatalogRowDensityChoices.First(choice => choice.Value == appearance.CatalogRowDensity);

            _savedSearchPageSize = indexing.SearchPageSize.ToString(CultureInfo.InvariantCulture);
            _savedMaxEntriesPerSecond = indexing.MaxEntriesPerSecond.ToString(CultureInfo.InvariantCulture);
            _savedDirectoryDelay = indexing.DirectoryDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            _savedUiUpdateBatchSize = indexing.UiUpdateBatchSize.ToString(CultureInfo.InvariantCulture);
            _savedNotificationAutoDismissSeconds = notifications.AutoDismissSeconds.ToString(CultureInfo.InvariantCulture);
            _savedTheme = appearance.Theme;
            _savedBackdrop = appearance.Backdrop;
            _savedCatalogRowDensity = appearance.CatalogRowDensity;
        }
        finally { _isRefreshing = false; }
        ValidateNumericDrafts();
    }

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);

    private sealed class DefaultNotificationSettings : IValidatedSettingsState<NotificationOptions>
    {
        public static DefaultNotificationSettings Instance { get; } = new();
        public NotificationOptions Current { get; } = new();
    }
}

public sealed record SettingChoice<T>(T Value, string DisplayName)
    where T : struct, Enum;
