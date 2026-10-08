using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed record ProfilePlaygroundEvaluation(
    string CanonicalPath,
    string Summary,
    InfoBarSeverity Severity,
    IReadOnlyList<ProfileTestResultViewModel> Rows,
    IReadOnlyList<ProfileRegexDebugRuleViewModel> RegexDebugRules,
    ProfileDirectoryExclusionTestResultViewModel DirectoryExclusionResult,
    IReadOnlyList<ProfileRegexDebugRuleViewModel> DirectoryExclusionRegexDebugRules);

public interface IProfilePlaygroundEvaluator
{
    ProfilePlaygroundEvaluation Evaluate(ILoadedProfile profile, string inputPath);
}

/// <summary>
/// Evaluates the exact profile instance currently published in the runtime catalog.
/// This deliberately does not rebuild an editor draft, so the playground and a real
/// structured scan always exercise the same compiled mapping contract.
/// </summary>
public sealed class ProfilePlaygroundEvaluator(
    IProfilePathCanonicalizer pathCanonicalizer,
    IAppLocalizer localizer) : IProfilePlaygroundEvaluator
{
    public ProfilePlaygroundEvaluation Evaluate(ILoadedProfile profile, string inputPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var canonicalPath = pathCanonicalizer.Canonicalize(inputPath.Trim());
        var descriptor = profile.Descriptor;
        var pathRules = descriptor.Rules
            .Select(static rule => new ProfileRegexRuleManifest
            {
                Id = rule.Id,
                Pattern = rule.Pattern,
                MatchMode = rule.MatchMode,
                IgnoreCase = rule.IgnoreCase,
                TimeoutMilliseconds = rule.TimeoutMilliseconds,
                StopTraversalWhenCapturedGroups =
                    rule.StopTraversalWhenCapturedGroups.ToList(),
            })
            .ToArray();
        var regexDebugRules = ProfileRegexDebugBuilder.Build(canonicalPath, pathRules);

        var leafName = GetDirectoryLeafName(canonicalPath);
        var exclusionRules = descriptor.ExcludedDirectoryNameRules
            .Select(static rule => new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = rule.Id,
                Pattern = rule.Pattern,
                MatchMode = rule.MatchMode,
                IgnoreCase = rule.IgnoreCase,
                TimeoutMilliseconds = rule.TimeoutMilliseconds,
            })
            .ToArray();
        var exclusionDebugRules = string.IsNullOrEmpty(leafName)
            ? Array.Empty<ProfileRegexDebugRuleViewModel>()
            : ProfileRegexDebugBuilder.BuildDirectoryName(leafName, exclusionRules);
        var exclusionResult = BuildDirectoryExclusionTestResult(
            leafName,
            exclusionRules.Length,
            profile,
            localizer);

        var mapping = profile.Map(new ProfilePathCandidate(canonicalPath));
        IReadOnlyList<ProfileTestResultViewModel> rows;
        string summary;
        InfoBarSeverity severity;
        switch (mapping.Status)
        {
            case ProfileMapStatus.NoMatch:
                rows = [];
                summary = L(
                    "Loc.Playground.Result.NoMatch",
                    "어떤 규칙에도 일치하지 않습니다. 절대 경로와 프로필 규칙을 확인하세요.");
                severity = InfoBarSeverity.Warning;
                break;

            case ProfileMapStatus.Invalid:
                rows = mapping.Issues
                    .Select(issue => new ProfileTestResultViewModel(
                        issue.FieldId ?? L("Loc.Profile.Rule", "규칙"),
                        null,
                        ProfileDiagnosticLocalizer.Translate(localizer, issue)))
                    .ToArray();
                summary = L(
                    "Loc.Playground.Result.Invalid",
                    "경로는 일치했지만 일부 값을 변환하거나 읽을 수 없습니다.");
                severity = InfoBarSeverity.Error;
                break;

            case ProfileMapStatus.Success when mapping.Item is not null:
                rows = descriptor.Fields
                    .OrderBy(static field => field.Order)
                    .ThenBy(static field => field.FieldId, StringComparer.Ordinal)
                    .Select(field =>
                    {
                        _ = mapping.Item.Values.TryGetValue(field.FieldId, out var rawValue);
                        _ = mapping.Item.DisplayValues.TryGetValue(
                            field.FieldId,
                            out var displayValue);
                        var rawText = FormatValue(rawValue, field.DisplayFormat);
                        var displayText = FormatValue(displayValue, field.DisplayFormat);
                        var hasMappedValue = !string.Equals(
                            rawText,
                            displayText,
                            StringComparison.Ordinal);
                        var source = field.SourceKind == ProfileFieldSourceKind.TextFileContent
                            ? L("Loc.Profile.Source.TextFile", "텍스트 파일")
                            : field.Required
                                ? L("Loc.Profile.Source.RequiredPath", "필수 경로 값")
                                : L("Loc.Profile.Source.OptionalPath", "선택 경로 값");
                        return new ProfileTestResultViewModel(
                            field.Header,
                            displayText,
                            hasMappedValue
                                ? F("Loc.Profile.Source.Mapped", "{0} · 표시 값 매핑", source)
                                : source,
                            rawText,
                            hasMappedValue);
                    })
                    .ToArray();
                summary = F(
                    "Loc.Playground.Result.Success",
                    "규칙 '{0}'에 일치했고 {1:N0}개 값을 불러왔습니다.",
                    mapping.Item.MatchedRuleId,
                    rows.Count);
                severity = InfoBarSeverity.Success;
                break;

            default:
                rows = [];
                summary = L(
                    "Loc.Playground.Result.Empty",
                    "프로필이 결과를 반환하지 않았습니다.");
                severity = InfoBarSeverity.Error;
                break;
        }

        return new ProfilePlaygroundEvaluation(
            canonicalPath,
            summary,
            severity,
            rows,
            regexDebugRules,
            exclusionResult,
            exclusionDebugRules);
    }

    internal static string GetDirectoryLeafName(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var root = Path.GetPathRoot(canonicalPath);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(canonicalPath),
                Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalPath));
    }

    internal static ProfileDirectoryExclusionTestResultViewModel
        BuildDirectoryExclusionTestResult(
            string directoryName,
            int ruleCount,
            ILoadedProfile? profile,
            IAppLocalizer? localizer = null)
    {
        var directScanScope = Get(
            localizer,
            "Loc.Playground.Exclusion.Scope",
            "이 판정은 구조화 보기의 ‘DB에 저장하지 않고 바로 스캔’에서 검색 루트 아래 폴더에만 적용됩니다. "
            + "입력 경로 자체를 검색 루트로 선택한 경우에는 명시적 루트 예외로 제외하지 않으며, "
            + "파일 인덱싱과 기존 인덱스 불러오기에도 적용하지 않습니다.");

        if (string.IsNullOrEmpty(directoryName))
        {
            return new ProfileDirectoryExclusionTestResultViewModel(
                Get(localizer, "Loc.Playground.Exclusion.Root", "(드라이브 또는 공유 루트)"),
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.NoLeaf",
                    "leaf 폴더 이름이 없어 제외 판정을 생략했습니다."),
                directScanScope,
                InfoBarSeverity.Informational);
        }

        if (ruleCount == 0)
        {
            return new ProfileDirectoryExclusionTestResultViewModel(
                directoryName,
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.NoRules",
                    "제외 규칙이 없어 이 폴더를 계속 탐색합니다."),
                directScanScope,
                InfoBarSeverity.Informational);
        }

        if (profile is null)
        {
            return new ProfileDirectoryExclusionTestResultViewModel(
                directoryName,
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.ProfileInvalid",
                    "프로필 정의 오류로 제외 여부를 판정할 수 없습니다."),
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.CheckRegex",
                    "아래 색상 디버깅과 검증 메시지에서 잘못된 정규식을 확인하세요. ")
                + directScanScope,
                InfoBarSeverity.Error);
        }

        var evaluation = profile.EvaluateDirectoryName(directoryName);
        if (evaluation.IsExcluded)
        {
            return new ProfileDirectoryExclusionTestResultViewModel(
                directoryName,
                Format(
                    localizer,
                    "Loc.Playground.Exclusion.Matched",
                    "규칙 ‘{0}’과 일치해 현재 폴더와 하위를 건너뜁니다.",
                    evaluation.MatchedRuleId),
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.SiblingsContinue",
                    "같은 부모의 다음 폴더 탐색은 계속합니다. ") + directScanScope,
                InfoBarSeverity.Success);
        }

        if (evaluation.Issues.Count > 0)
        {
            return new ProfileDirectoryExclusionTestResultViewModel(
                directoryName,
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.Unsafe",
                    "제외 규칙을 안전하게 판정하지 못해 이 폴더를 계속 탐색합니다."),
                string.Join(
                    Environment.NewLine,
                    evaluation.Issues.Select(issue =>
                        $"• {ProfileDiagnosticLocalizer.Translate(localizer, issue)}"))
                + Environment.NewLine
                + directScanScope,
                InfoBarSeverity.Warning);
        }

        return new ProfileDirectoryExclusionTestResultViewModel(
            directoryName,
            Get(
                localizer,
                "Loc.Playground.Exclusion.NoMatch",
                "어떤 제외 규칙에도 일치하지 않아 이 폴더를 계속 탐색합니다."),
            directScanScope,
            InfoBarSeverity.Informational);
    }

    private string L(string key, string koreanFallback) =>
        localizer.Get(key, koreanFallback);

    private string F(string key, string koreanFallback, params object?[] arguments) =>
        localizer.Format(key, koreanFallback, arguments);

    private static string Get(
        IAppLocalizer? localizer,
        string key,
        string koreanFallback) =>
        localizer?.Get(key, koreanFallback) ?? koreanFallback;

    private static string Format(
        IAppLocalizer? localizer,
        string key,
        string koreanFallback,
        params object?[] arguments) =>
        localizer?.Format(key, koreanFallback, arguments)
        ?? string.Format(CultureInfo.CurrentCulture, koreanFallback, arguments);

    private static string FormatValue(object? value, string? displayFormat)
    {
        if (value is null)
        {
            return "—";
        }

        try
        {
            return value is IFormattable formattable
                ? formattable.ToString(displayFormat, CultureInfo.CurrentCulture) ?? string.Empty
                : Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }
        catch (FormatException)
        {
            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }
    }
}

public sealed class ProfileDirectoryExclusionTestResultViewModel(
    string leafName,
    string title,
    string message,
    InfoBarSeverity severity)
{
    public string LeafName { get; } = leafName;

    public string Title { get; } = title;

    public string Message { get; } = message;

    public InfoBarSeverity Severity { get; } = severity;
}

public sealed class ProfilePlaygroundProfileViewModel(
    ILoadedProfile profile,
    IAppLocalizer? localizer = null)
{
    internal ILoadedProfile Profile { get; } = profile;

    public string Id => Profile.Descriptor.Id;

    public string DisplayName => Profile.Descriptor.DisplayName;

    public string Description => localizer?.Format(
        "Loc.Playground.Profile.Description",
        "{0} · {1:N0}개 컬럼",
        DisplayName,
        Profile.Descriptor.Fields.Count)
        ?? $"{DisplayName} · {Profile.Descriptor.Fields.Count:N0}개 컬럼";
}

public partial class ProfilePlaygroundViewModel : ObservableObject
{
    private readonly IProfileCatalog _profileCatalog;
    private readonly IProfilePlaygroundEvaluator _evaluator;
    private readonly IDesktopPickerService _pickerService;
    private readonly IAppLocalizer _localizer;
    private readonly ILogger<ProfilePlaygroundViewModel> _logger;
    private string? _preferredProfileId;
    private long _evaluationGeneration;

    [ObservableProperty]
    private IReadOnlyList<ProfilePlaygroundProfileViewModel> _profiles = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    private ProfilePlaygroundProfileViewModel? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EvaluateCommand))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = "프로필과 실제 절대 경로를 선택해 결과를 확인하세요.";

    [ObservableProperty]
    private InfoBarSeverity _summarySeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private IReadOnlyList<ProfileTestResultViewModel> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileRegexDebugRuleViewModel> _regexDebugRules = [];

    [ObservableProperty]
    private string _regexDebugInputPath = string.Empty;

    [ObservableProperty]
    private ProfileDirectoryExclusionTestResultViewModel? _directoryExclusionTestResult;

    [ObservableProperty]
    private IReadOnlyList<ProfileRegexDebugRuleViewModel>
        _directoryExclusionRegexDebugRules = [];

    public ProfilePlaygroundViewModel(
        IProfileCatalog profileCatalog,
        IProfilePlaygroundEvaluator evaluator,
        IDesktopPickerService pickerService,
        IAppLocalizer localizer,
        ILogger<ProfilePlaygroundViewModel> logger)
    {
        _profileCatalog = profileCatalog;
        _evaluator = evaluator;
        _pickerService = pickerService;
        _localizer = localizer;
        _logger = logger;
        Summary = L(
            "Loc.Playground.Status.Initial",
            "프로필과 실제 절대 경로를 선택해 결과를 확인하세요.");
        ApplyCatalog(profileCatalog.Current);
        _profileCatalog.Changed += OnProfileCatalogChanged;
    }

    public bool HasRegexDebugRules => RegexDebugRules.Count > 0;

    public bool IsReady => !IsBusy;

    public bool HasDirectoryExclusionTestResult => DirectoryExclusionTestResult is not null;

    public bool HasDirectoryExclusionRegexDebugRules =>
        DirectoryExclusionRegexDebugRules.Count > 0;

    partial void OnSelectedProfileChanged(ProfilePlaygroundProfileViewModel? value)
    {
        _preferredProfileId = value?.Id ?? _preferredProfileId;
        InvalidateEvaluation(L(
            "Loc.Playground.Status.ProfileChanged",
            "프로필이 변경되었습니다. 같은 경로를 다시 확인하세요."));
    }

    partial void OnInputPathChanged(string value) =>
        InvalidateEvaluation(L(
            "Loc.Playground.Status.PathChanged",
            "경로가 변경되었습니다. 결과를 다시 확인하세요."));

    partial void OnRegexDebugRulesChanged(
        IReadOnlyList<ProfileRegexDebugRuleViewModel> value) =>
        OnPropertyChanged(nameof(HasRegexDebugRules));

    partial void OnDirectoryExclusionTestResultChanged(
        ProfileDirectoryExclusionTestResultViewModel? value) =>
        OnPropertyChanged(nameof(HasDirectoryExclusionTestResult));

    partial void OnDirectoryExclusionRegexDebugRulesChanged(
        IReadOnlyList<ProfileRegexDebugRuleViewModel> value) =>
        OnPropertyChanged(nameof(HasDirectoryExclusionRegexDebugRules));

    public bool SelectProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var profile = Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return false;
        }

        _preferredProfileId = profile.Id;
        SelectedProfile = profile;
        return true;
    }

    [RelayCommand]
    private void BrowsePath()
    {
        var selected = _pickerService.PickRootDirectory(InputPath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            InputPath = selected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private async Task EvaluateAsync()
    {
        var selectedProfile = SelectedProfile;
        if (selectedProfile is null)
        {
            return;
        }

        var inputPath = InputPath;
        var evaluationGeneration = Interlocked.Increment(ref _evaluationGeneration);
        IsBusy = true;
        Summary = L(
            "Loc.Playground.Status.Evaluating",
            "저장된 프로필로 절대 경로를 확인하고 있습니다.");
        SummarySeverity = InfoBarSeverity.Informational;
        try
        {
            var evaluation = await Task.Run(() =>
                _evaluator.Evaluate(selectedProfile.Profile, inputPath)).ConfigureAwait(true);

            if (evaluationGeneration != Volatile.Read(ref _evaluationGeneration))
            {
                return;
            }

            RegexDebugInputPath = evaluation.CanonicalPath;
            RegexDebugRules = evaluation.RegexDebugRules;
            DirectoryExclusionTestResult = evaluation.DirectoryExclusionResult;
            DirectoryExclusionRegexDebugRules =
                evaluation.DirectoryExclusionRegexDebugRules;
            Rows = evaluation.Rows;
            Summary = evaluation.Summary;
            SummarySeverity = evaluation.Severity;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Profile playground evaluation failed.");
            if (evaluationGeneration != Volatile.Read(ref _evaluationGeneration))
            {
                return;
            }

            ClearResults(
                L(
                    "Loc.Playground.Status.InvalidInput",
                    "이 경로를 사용할 수 없습니다. 드라이브 또는 UNC 루트를 포함한 실제 절대 경로를 확인하세요."),
                InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Profile playground evaluation failed unexpectedly.");
            if (evaluationGeneration != Volatile.Read(ref _evaluationGeneration))
            {
                return;
            }

            ClearResults(
                L(
                    "Loc.Playground.Status.Failed",
                    "프로필 테스트를 완료하지 못했습니다. 상세 로그를 확인하세요."),
                InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InvalidateEvaluation(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _ = Interlocked.Increment(ref _evaluationGeneration);
        ClearResults(message, severity);
    }

    private bool CanEvaluate() =>
        !IsBusy
        && SelectedProfile is not null
        && !string.IsNullOrWhiteSpace(InputPath);

    private void OnProfileCatalogChanged(object? sender, ProfileCatalogChangedEventArgs eventArgs)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyCatalog(eventArgs.Current);
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(() => ApplyCatalog(eventArgs.Current));
        }
    }

    private void ApplyCatalog(ProfileCatalogSnapshot snapshot)
    {
        var preferredId = _preferredProfileId ?? SelectedProfile?.Id;
        Profiles = snapshot.Profiles
            .Select(profile => new ProfilePlaygroundProfileViewModel(profile, _localizer))
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedProfile = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
    }

    private void ClearResults(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Rows = [];
        RegexDebugRules = [];
        RegexDebugInputPath = string.Empty;
        DirectoryExclusionRegexDebugRules = [];
        DirectoryExclusionTestResult = null;
        Summary = message;
        SummarySeverity = severity;
    }

    private string L(string key, string koreanFallback) =>
        _localizer.Get(key, koreanFallback);
}
