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

public partial class OutputFormatsViewModel : ObservableObject
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
        ILogger<OutputFormatsViewModel> logger)
    {
        _formatStore = formatStore;
        _formatter = formatter;
        _profileCatalog = profileCatalog;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _localizer = localizer;
        _logger = logger;
        StatusMessage = L(
            "Loc.Output.Status.Initial",
            "기본 포맷을 그대로 사용하거나 새 출력 포맷을 만드세요.");

        RefreshProfileChoices(preferredProfileId: null);
        RefreshFormats(SelectionOutputFormatDefaults.FullPathLinesId);
        _formatStore.Changed += OnFormatStoreChanged;
        _profileCatalog.Changed += OnProfileCatalogChanged;
    }

    public bool IsDeleteAvailable => CanEdit && SelectedFormat is not null;

    public bool HasProfileTokens => TokenChoices.Count > 3;

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

        PopulateDraft(value.Definition);
    }

    partial void OnSelectedProfileChoiceChanged(OutputFormatProfileChoice? value)
    {
        RefreshTokenChoices(value);
        if (!_isPopulatingDraft)
        {
            SetDraftChangedStatus();
        }
    }

    partial void OnDraftDisplayNameChanged(string value) => SetDraftChangedStatus();

    partial void OnDraftTemplateChanged(string value) => SetDraftChangedStatus();

    partial void OnDraftItemSeparatorChanged(string value) => SetDraftChangedStatus();

    partial void OnCanEditChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        InsertTokenCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDeleteAvailable));
    }

    [RelayCommand]
    private void NewFormat()
    {
        _isPopulatingDraft = true;
        try
        {
            SelectedFormat = null;
            _draftId = $"format.{Guid.NewGuid():N}";
            CanEdit = true;
            DraftDisplayName = L("Loc.Output.New.DefaultName", "새 출력 포맷");
            DraftTemplate = "{FullPath}";
            DraftItemSeparator = "\\r\\n";
            SelectedProfileChoice = ProfileChoices.FirstOrDefault();
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
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void InsertToken(OutputFormatTokenChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        DraftTemplate += choice.Token;
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void Save()
    {
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
            var validation = _formatter.ValidateTemplate(DraftTemplate, allowedFields);
            if (!validation.IsValid)
            {
                SetError(string.Join(" ", validation.Errors));
                return;
            }

            var definition = new SelectionOutputFormatDefinition(
                _draftId,
                displayName,
                DraftTemplate,
                DecodeSeparator(DraftItemSeparator),
                SelectedProfileChoice?.ProfileId);
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

    private bool CanModify() => CanEdit;

    private bool CanDeleteFormat() => IsDeleteAvailable;

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
    }

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

    private static void RunOnDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }

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
