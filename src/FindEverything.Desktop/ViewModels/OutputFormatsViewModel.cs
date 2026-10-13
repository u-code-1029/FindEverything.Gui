using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed record OutputFormatSummaryViewModel(
    SelectionOutputFormatDefinition Definition,
    string ScopeText,
    string BuiltInDetail)
{
    public string DisplayName => Definition.DisplayName;

    public string Detail => Definition.IsBuiltIn ? BuiltInDetail : ScopeText;
}

public sealed record OutputFormatProfileChoice(
    string? ProfileId,
    string DisplayName,
    IReadOnlyList<ProfileFieldDescriptor> Fields)
{
    public bool IsCommon => string.IsNullOrWhiteSpace(ProfileId);
}

public sealed record OutputFormatTokenChoice(
    string DisplayName,
    string Token,
    string Description);

public partial class OutputFormatsViewModel : ObservableObject, IDraftParticipant
{
    private readonly ISelectionOutputFormatStore _formatStore;
    private readonly ISelectionOutputFormatter _formatter;
    private readonly IProfileCatalog _profileCatalog;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<OutputFormatsViewModel> _logger;
    private string _draftId = string.Empty;
    private bool _isPopulatingDraft;
    private bool _isDraftDirty;
    private readonly IDraftProtectionService? _draftProtection;
    private OutputFormatSummaryViewModel? _loadedFormat;
    private readonly IUiScheduler _uiScheduler;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isTransitioning;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private string _previewValidationMessage = string.Empty;

    [ObservableProperty]
    private bool _isPreviewValid;

    [ObservableProperty]
    private IReadOnlyList<OutputFormatSummaryViewModel> _formats = [];

    [ObservableProperty]
    private OutputFormatSummaryViewModel? _selectedFormat;

    [ObservableProperty]
    private IReadOnlyList<OutputFormatProfileChoice> _profileChoices = [];

    [ObservableProperty]
    private OutputFormatProfileChoice? _selectedProfileChoice;

    [ObservableProperty]
    private IReadOnlyList<OutputFormatTokenChoice> _tokenChoices = [];

    [ObservableProperty]
    private string _draftDisplayName = string.Empty;

    [ObservableProperty]
    private string _draftTemplate = string.Empty;

    [ObservableProperty]
    private string _draftItemSeparator = "\\r\\n";

    [ObservableProperty]
    private string _draftPrefix = string.Empty;

    [ObservableProperty]
    private string _draftSuffix = string.Empty;

    [ObservableProperty]
    private bool _canEdit;

    [ObservableProperty]
    private string _statusMessage = "기본 포맷을 그대로 사용하거나 새 출력 포맷을 만드세요.";

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public OutputFormatsViewModel(
        ISelectionOutputFormatStore formatStore,
        ISelectionOutputFormatter formatter,
        IProfileCatalog profileCatalog,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppLocalizer localizer,
        ILogger<OutputFormatsViewModel> logger,
        IDraftProtectionService? draftProtection = null,
        IUiScheduler? uiScheduler = null)
    {
        _formatStore = formatStore;
        _formatter = formatter;
        _profileCatalog = profileCatalog;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _localizer = localizer;
        _logger = logger;
        _draftProtection = draftProtection;
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;
        _draftProtection?.Register(this);
        StatusMessage = L(
            "Loc.Output.Status.Initial",
            "기본 포맷을 그대로 사용하거나 새 출력 포맷을 만드세요.");

        RefreshProfileChoices(preferredProfileId: null);
        RefreshFormats(SelectionOutputFormatDefaults.FullPathLinesId);
        _formatStore.Changed += OnFormatStoreChanged;
        _profileCatalog.Changed += OnProfileCatalogChanged;
    }

    public bool IsDeleteAvailable => CanEdit && SelectedFormat is not null;

    public bool HasProfileTokens => TokenChoices.Count > 4;

    public IReadOnlyList<OutputFormatTokenChoice> CommonTokenChoices => TokenChoices.Take(4).ToArray();

    public IReadOnlyList<OutputFormatTokenChoice> ProfileTokenChoices => TokenChoices.Skip(4).ToArray();

    public string ProfileColumnGuidance => HasProfileTokens
        ? F("Loc.Output.Profile.ColumnsReady", "'{0}'의 컬럼을 클릭하면 현재 커서 위치에 삽입됩니다.", SelectedProfileChoice?.DisplayName)
        : L("Loc.Output.Profile.ChooseForColumns", "구조화된 프로필을 선택하면 해당 프로필의 컬럼 버튼이 나타납니다.");

    public bool IsEditorReady => !IsSaving && !IsTransitioning;

    public string DraftTitle => DraftDisplayName;

    public bool HasUnsavedChanges => _isDraftDirty;

    public Task<bool> SaveChangesAsync()
    {
        if (IsSaving || !CanEdit) return Task.FromResult(false);
        Save();
        return Task.FromResult(!_isDraftDirty);
    }

    public void DiscardChanges() => _isDraftDirty = false;

    partial void OnSelectedFormatChanged(OutputFormatSummaryViewModel? value)
    {
        OnPropertyChanged(nameof(IsDeleteAvailable));
        DeleteCommand.NotifyCanExecuteChanged();
        if (_isPopulatingDraft)
        {
            return;
        }

        if (value is null)
        {
            return;
        }

        if (_isDraftDirty && _draftProtection is not null)
        {
            _isPopulatingDraft = true;
            SelectedFormat = _loadedFormat;
            _isPopulatingDraft = false;
            _ = SwitchFormatAsync(value);
            return;
        }

        _loadedFormat = value;
        PopulateDraft(value.Definition);
    }

    partial void OnSelectedProfileChoiceChanged(OutputFormatProfileChoice? value)
    {
        RefreshTokenChoices(value);
        RefreshPreview();
        if (!_isPopulatingDraft)
        {
            SetDraftChangedStatus();
        }
    }

    partial void OnDraftDisplayNameChanged(string value) => SetDraftChangedStatus();

    partial void OnDraftTemplateChanged(string value)
    {
        SetDraftChangedStatus();
        RefreshPreview();
    }

    partial void OnDraftItemSeparatorChanged(string value)
    {
        SetDraftChangedStatus();
        RefreshPreview();
    }

    partial void OnDraftPrefixChanged(string value)
    {
        SetDraftChangedStatus();
        RefreshPreview();
    }

    partial void OnDraftSuffixChanged(string value)
    {
        SetDraftChangedStatus();
        RefreshPreview();
    }

    partial void OnIsSavingChanged(bool value) => NotifyEditorAvailability();

    partial void OnIsTransitioningChanged(bool value) => NotifyEditorAvailability();

    partial void OnCanEditChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        InsertTokenCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDeleteAvailable));
    }

    [RelayCommand(CanExecute = nameof(CanStartTransition))]
    private async Task NewFormatAsync()
    {
        IsTransitioning = true;
        try
        {
            if (_draftProtection is not null
                && !await _draftProtection.ConfirmAsync(this).ConfigureAwait(true)) return;
        }
        finally { IsTransitioning = false; }
        _isPopulatingDraft = true;
        try
        {
            SelectedFormat = null;
            _loadedFormat = null;
            _draftId = $"format.{Guid.NewGuid():N}";
            CanEdit = true;
            DraftDisplayName = L("Loc.Output.New.DefaultName", "새 출력 포맷");
            DraftTemplate = "{FullPath}";
            DraftItemSeparator = "\\r\\n";
            DraftPrefix = string.Empty;
            DraftSuffix = string.Empty;
            SelectedProfileChoice = ProfileChoices.FirstOrDefault(static choice => !choice.IsCommon)
                ?? ProfileChoices.FirstOrDefault();
            RefreshTokenChoices(SelectedProfileChoice);
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        StatusSeverity = InfoBarSeverity.Informational;
        StatusMessage = L(
            "Loc.Output.New.Instruction",
            "이름과 템플릿을 정한 뒤 저장하세요.");
        _isDraftDirty = true;
        RefreshPreview();
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void InsertToken(OutputFormatTokenChoice? choice)
    {
        InsertTokenAtSelection(choice, DraftTemplate.Length, 0);
    }

    public int InsertTokenAtSelection(OutputFormatTokenChoice? choice, int selectionStart, int selectionLength)
    {
        if (!CanModify() || choice is null) return Math.Clamp(selectionStart, 0, DraftTemplate.Length);
        var start = Math.Clamp(selectionStart, 0, DraftTemplate.Length);
        var length = Math.Clamp(selectionLength, 0, DraftTemplate.Length - start);
        DraftTemplate = DraftTemplate.Remove(start, length).Insert(start, choice.Token);
        return start + choice.Token.Length;
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void Save()
    {
        if (IsSaving) return;
        IsSaving = true;
        try
        {
            var displayName = DraftDisplayName.Trim();
            if (displayName.Length == 0)
            {
                SetError(L("Loc.Output.Error.NameRequired", "출력 포맷 이름을 입력하세요."));
                return;
            }

            var allowedFields = SelectedProfileChoice?.Fields
                .Select(static field => field.FieldId)
                ?? [];
            var definition = CreateDraftDefinition(_draftId, displayName);
            var validation = _formatter.ValidateFormat(definition, allowedFields);
            if (!validation.IsValid)
            {
                SetError(string.Join(" ", validation.Errors));
                return;
            }

            _formatStore.Save(definition);
            _isDraftDirty = false;
            RefreshFormats(definition.Id);
            StatusSeverity = InfoBarSeverity.Success;
            StatusMessage = F(
                "Loc.Output.Save.Message",
                "'{0}' 포맷을 저장했습니다.",
                definition.DisplayName);
            _snackbarService.Show(
                L("Loc.Output.Save.Completed", "출력 포맷 저장 완료"),
                StatusMessage,
                ControlAppearance.Success,
                null,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            _logger.LogError(exception, "Could not save selection output format {FormatId}.", _draftId);
            SetError(exception.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteFormat))]
    private async Task DeleteAsync()
    {
        var selected = SelectedFormat;
        if (selected is null || selected.Definition.IsBuiltIn)
        {
            return;
        }

        var result = await _contentDialogService.ShowAsync(
            new ContentDialog
            {
                Title = L("Loc.Output.Delete.Title", "출력 포맷을 삭제할까요?"),
                Content = F(
                    "Loc.Output.Delete.Message",
                    "'{0}' 포맷을 삭제합니다.",
                    selected.DisplayName),
                PrimaryButtonText = L("Loc.Common.Delete", "삭제"),
                CloseButtonText = L("Loc.Common.Cancel", "취소"),
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonAppearance = ControlAppearance.Danger,
            },
            CancellationToken.None).ConfigureAwait(true);
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (_formatStore.Delete(selected.Definition.Id))
            {
                _isDraftDirty = false;
                RefreshFormats(SelectionOutputFormatDefaults.FullPathLinesId);
                StatusSeverity = InfoBarSeverity.Success;
                StatusMessage = F(
                    "Loc.Output.Delete.Completed",
                    "'{0}' 포맷을 삭제했습니다.",
                    selected.DisplayName);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            _logger.LogError(
                exception,
                "Could not delete selection output format {FormatId}.",
                selected.Definition.Id);
            SetError(exception.Message);
        }
    }

    private bool CanModify() => CanEdit && IsEditorReady;

    private bool CanDeleteFormat() => IsDeleteAvailable && IsEditorReady;

    private bool CanStartTransition() => IsEditorReady;

    private void NotifyEditorAvailability()
    {
        OnPropertyChanged(nameof(IsEditorReady));
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        InsertTokenCommand.NotifyCanExecuteChanged();
        NewFormatCommand.NotifyCanExecuteChanged();
    }

    private async Task SwitchFormatAsync(OutputFormatSummaryViewModel value)
    {
        if (IsTransitioning || IsSaving) return;
        IsTransitioning = true;
        try
        {
            if (!await _draftProtection!.ConfirmAsync(this).ConfigureAwait(true)) return;
            var currentValue = Formats.FirstOrDefault(format =>
                string.Equals(format.Definition.Id, value.Definition.Id, StringComparison.OrdinalIgnoreCase));
            if (currentValue is null)
            {
                SetError(L("Loc.Output.Error.TargetMissing", "선택한 포맷이 더 이상 없습니다. 현재 초안을 유지합니다."));
                return;
            }
            _isPopulatingDraft = true;
            SelectedFormat = currentValue;
            _isPopulatingDraft = false;
            _loadedFormat = currentValue;
            PopulateDraft(currentValue.Definition);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not leave output format draft.");
            SetError(L("Loc.Output.Error.LeaveFailed", "변경 사항을 확인하지 못했습니다. 현재 초안을 유지합니다."));
        }
        finally { IsTransitioning = false; }
    }

    private void PopulateDraft(SelectionOutputFormatDefinition definition)
    {
        _isPopulatingDraft = true;
        try
        {
            _draftId = definition.Id;
            CanEdit = !definition.IsBuiltIn;
            DraftDisplayName = definition.DisplayName;
            DraftTemplate = definition.Template;
            DraftItemSeparator = EncodeSeparator(definition.ItemSeparator);
            DraftPrefix = EncodeSeparator(definition.Prefix ?? string.Empty);
            DraftSuffix = EncodeSeparator(definition.Suffix ?? string.Empty);
            SelectedProfileChoice = ProfileChoices.FirstOrDefault(choice =>
                string.Equals(
                    choice.ProfileId,
                    definition.ProfileId,
                    StringComparison.OrdinalIgnoreCase)) ?? ProfileChoices.FirstOrDefault();
            RefreshTokenChoices(SelectedProfileChoice);
        }
        finally
        {
            _isPopulatingDraft = false;
        }

        _isDraftDirty = false;
        RefreshPreview();

        StatusSeverity = InfoBarSeverity.Informational;
        StatusMessage = definition.IsBuiltIn
            ? L(
                "Loc.Output.BuiltIn.Detail",
                "기본 포맷은 모든 페이지에서 사용할 수 있으며 수정하거나 삭제할 수 없습니다.")
            : L(
                "Loc.Output.Edit.Detail",
                "포맷을 편집한 뒤 저장하면 파일 찾기와 구조화 보기에서 바로 사용할 수 있습니다.");
    }

    private void RefreshFormats(string? preferredId)
    {
        var profileNames = _profileCatalog.Current.Profiles.ToDictionary(
            static profile => profile.Descriptor.Id,
            static profile => profile.Descriptor.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        Formats = _formatStore.GetAll()
            .Select(format => new OutputFormatSummaryViewModel(
                format,
                format.ProfileId is not null
                    && profileNames.TryGetValue(format.ProfileId, out var displayName)
                        ? F("Loc.Output.Scope.Profile", "프로필 · {0}", displayName)
                        : string.IsNullOrWhiteSpace(format.ProfileId)
                            ? L("Loc.Output.Scope.AllPages", "모든 페이지")
                            : F("Loc.Output.Scope.Profile", "프로필 · {0}", format.ProfileId),
                L("Loc.Output.Scope.BuiltIn", "기본 제공 · 모든 페이지")))
            .ToArray();
        var preferred = Formats.FirstOrDefault(format =>
            string.Equals(
                format.Definition.Id,
                preferredId,
                StringComparison.OrdinalIgnoreCase));
        if (_isDraftDirty)
        {
            _isPopulatingDraft = true;
            try
            {
                SelectedFormat = preferred;
                _loadedFormat = preferred;
            }
            finally
            {
                _isPopulatingDraft = false;
            }

            return;
        }

        SelectedFormat = preferred ?? Formats.FirstOrDefault();
    }

    private void RefreshProfileChoices(string? preferredProfileId)
    {
        var choices = new List<OutputFormatProfileChoice>
        {
            new(
                null,
                L("Loc.Output.Profile.Common", "공통 필드만 사용 (모든 페이지)"),
                []),
        };
        choices.AddRange(_profileCatalog.Current.Profiles
            .OrderBy(static profile => profile.Descriptor.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static profile => new OutputFormatProfileChoice(
                profile.Descriptor.Id,
                profile.Descriptor.DisplayName,
                profile.Descriptor.Fields
                    .OrderBy(static field => field.Order)
                    .ToArray())));
        ProfileChoices = choices;
        SelectedProfileChoice = choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.ProfileId,
                    preferredProfileId,
                    StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
        RefreshTokenChoices(SelectedProfileChoice);
    }

    private void RefreshTokenChoices(OutputFormatProfileChoice? profile)
    {
        var choices = new List<OutputFormatTokenChoice>
        {
            new(
                L("Loc.Output.Token.FullPath", "전체 경로"),
                "{FullPath}",
                L("Loc.Output.Token.FullPath.Detail", "파일 또는 구조화 항목의 전체 경로")),
            new(
                L("Loc.Output.Token.OriginalFullPath", "원본 전체 경로"),
                "{OriginalFullPath}",
                L("Loc.Output.Token.OriginalFullPath.Help", "공유 데이터를 보낸 PC의 원래 경로입니다. 로컬 항목은 전체 경로와 같습니다.")),
            new(
                L("Loc.Output.Token.FolderPath", "폴더 경로"),
                "{FolderPath}",
                L("Loc.Output.Token.FolderPath.Detail", "항목이 들어 있는 폴더 경로")),
            new(
                L("Loc.Output.Token.Name", "이름"),
                "{Name}",
                L("Loc.Output.Token.Name.Detail", "파일 또는 폴더 이름")),
        };
        if (profile is not null)
        {
            choices.AddRange(profile.Fields.Select(field => new OutputFormatTokenChoice(
                field.Header,
                $"{{Field:{field.FieldId}}}",
                $"{profile.DisplayName} · {field.FieldId}")));
        }

        TokenChoices = choices;
        OnPropertyChanged(nameof(HasProfileTokens));
        OnPropertyChanged(nameof(CommonTokenChoices));
        OnPropertyChanged(nameof(ProfileTokenChoices));
        OnPropertyChanged(nameof(ProfileColumnGuidance));
    }

    private void RefreshPreview()
    {
        var fields = SelectedProfileChoice?.Fields ?? [];
        var definition = CreateDraftDefinition("preview", DraftDisplayName);
        var validation = _formatter.ValidateFormat(definition, fields.Select(static field => field.FieldId));
        IsPreviewValid = validation.IsValid;
        PreviewValidationMessage = string.Join(Environment.NewLine, validation.Errors);
        if (!validation.IsValid)
        {
            PreviewText = string.Empty;
            return;
        }

        var items = Enumerable.Range(1, 2).Select(index => SelectionOutputItem.FromPath(
            $@"C:\Example\Project-{index}",
            $@"C:\Example\Project-{index}",
            fields.ToDictionary(static field => field.FieldId,
                field => (object?)$"{field.Header} {index}", StringComparer.OrdinalIgnoreCase)));
        try
        {
            PreviewText = _formatter.Format(definition, items);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            IsPreviewValid = false;
            PreviewValidationMessage = exception.Message;
            PreviewText = string.Empty;
        }
    }

    private SelectionOutputFormatDefinition CreateDraftDefinition(string id, string displayName) => new(
        id, displayName, DraftTemplate, DecodeSeparator(DraftItemSeparator), SelectedProfileChoice?.ProfileId,
        DecodeSeparator(DraftPrefix), DecodeSeparator(DraftSuffix));

    private void OnFormatStoreChanged(object? sender, EventArgs e) =>
        RunOnDispatcher(() => RefreshFormats(_draftId));

    private void OnProfileCatalogChanged(object? sender, ProfileCatalogChangedEventArgs e) =>
        RunOnDispatcher(() =>
        {
            var profileId = SelectedProfileChoice?.ProfileId;
            _isPopulatingDraft = true;
            try
            {
                RefreshProfileChoices(profileId);
            }
            finally
            {
                _isPopulatingDraft = false;
            }

            RefreshFormats(SelectedFormat?.Definition.Id);
        });

    private void RunOnDispatcher(Action action) => _uiScheduler.Post(action);

    private void SetDraftChangedStatus()
    {
        if (_isPopulatingDraft || !CanEdit)
        {
            return;
        }

        StatusSeverity = InfoBarSeverity.Informational;
        StatusMessage = L(
            "Loc.Output.Status.Dirty",
            "변경 사항을 저장하면 결과 페이지의 포맷 목록에 반영됩니다.");
        _isDraftDirty = true;
    }

    private void SetError(string message)
    {
        StatusSeverity = InfoBarSeverity.Error;
        StatusMessage = message;
        _snackbarService.Show(
            L("Loc.Output.Error.Title", "출력 포맷을 확인해 주세요"),
            message,
            ControlAppearance.Danger,
            null,
            TimeSpan.FromSeconds(4));
    }

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        _localizer.Format(key, koreanFallback, arguments);

    internal static string EncodeSeparator(string separator) => separator
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    internal static string DecodeSeparator(string separator)
    {
        var builder = new System.Text.StringBuilder(separator.Length);
        for (var index = 0; index < separator.Length; index++)
        {
            if (separator[index] != '\\' || index + 1 >= separator.Length)
            {
                builder.Append(separator[index]);
                continue;
            }

            var escaped = separator[++index];
            builder.Append(escaped switch
            {
                'r' => '\r',
                'n' => '\n',
                't' => '\t',
                '\\' => '\\',
                _ => escaped,
            });
        }

        return builder.ToString();
    }
}
