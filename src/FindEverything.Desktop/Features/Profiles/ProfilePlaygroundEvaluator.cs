using System.Globalization;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed record ProfilePlaygroundEvaluation(
    string CanonicalPath,
    string Summary,
    InfoBarSeverity Severity,
    IReadOnlyList<ProfileTestResultViewModel> Rows,
    IReadOnlyList<ProfileRegexDebugRuleViewModel> RegexDebugRules,
    ProfileDirectoryExclusionTestResultViewModel DirectoryExclusionResult,
    IReadOnlyList<ProfileRegexDebugRuleViewModel> DirectoryExclusionRegexDebugRules)
{
    public MappedProfileItem? SampleItem { get; init; }
}

public interface IProfilePlaygroundEvaluator
{
    ProfilePlaygroundEvaluation Evaluate(ILoadedProfile profile, string inputPath);

    /// <summary>Runs synchronous profile/file evaluation away from the caller's UI context.</summary>
    Task<ProfilePlaygroundEvaluation> EvaluateAsync(ILoadedProfile profile, string inputPath) =>
        Task.Run(() => Evaluate(profile, inputPath));

    async Task<ProfilePlaygroundEvaluation> EvaluateAsync(ILoadedProfile profile, string inputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Do not abandon the actual worker via WaitAsync: an uninterruptible OS
        // call must still occupy the single evaluation slot until it returns.
        var result = await EvaluateAsync(profile, inputPath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>
/// Evaluates the exact profile instance currently published in the runtime catalog.
/// This deliberately does not rebuild an editor draft, so the playground and a real
/// structured scan always exercise the same compiled mapping contract.
/// </summary>
public sealed class ProfilePlaygroundEvaluator(
    IProfilePathCanonicalizer pathCanonicalizer,
    IAppLocalizer localizer,
    IProfileSampleEvaluator? sampleEvaluator = null) : IProfilePlaygroundEvaluator
{
    public ProfilePlaygroundEvaluation Evaluate(ILoadedProfile profile, string inputPath) =>
        EvaluateCore(profile, inputPath, CancellationToken.None);

    public Task<ProfilePlaygroundEvaluation> EvaluateAsync(ILoadedProfile profile, string inputPath,
        CancellationToken cancellationToken) => Task.Run(() => EvaluateCore(profile, inputPath, cancellationToken), cancellationToken);

    private ProfilePlaygroundEvaluation EvaluateCore(ILoadedProfile profile, string inputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var sample = (sampleEvaluator ?? new ProfileSampleEvaluator(pathCanonicalizer)).Evaluate(profile, inputPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = sample.CanonicalPath;
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
        cancellationToken.ThrowIfCancellationRequested();

        var leafName = sample.LeafName;
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
            localizer,
            sample.DirectoryExclusion);
        cancellationToken.ThrowIfCancellationRequested();

        var mapping = sample.Mapping;
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
                        object? rawValue;
                        object? displayValue;
                        if (field.SourceKind == ProfileFieldSourceKind.UserInput)
                        {
                            rawValue = field.DefaultValue;
                            displayValue = field.DefaultValue;
                        }
                        else
                        {
                            _ = mapping.Item.Values.TryGetValue(field.FieldId, out rawValue);
                            _ = mapping.Item.DisplayValues.TryGetValue(
                                field.FieldId,
                                out displayValue);
                        }
                        var rawText = FormatValue(rawValue, field.DisplayFormat);
                        var displayText = FormatValue(displayValue, field.DisplayFormat);
                        var hasMappedValue = !string.Equals(
                            rawText,
                            displayText,
                            StringComparison.Ordinal);
                        var source = field.SourceKind switch
                        {
                            ProfileFieldSourceKind.TextFileContent =>
                                L("Loc.Profile.Source.TextFile", "텍스트 파일"),
                            ProfileFieldSourceKind.UserInput =>
                                L("Loc.Profile.Source.UserInput", "사용자 입력"),
                            ProfileFieldSourceKind.ChildDirectoryExists =>
                                L("Loc.Profile.Source.ChildDirectory", "바로 아래 폴더 존재 여부"),
                            _ when field.Required =>
                                L("Loc.Profile.Source.RequiredPath", "필수 경로 값"),
                            _ => L("Loc.Profile.Source.OptionalPath", "선택 경로 값"),
                        };
                        return new ProfileTestResultViewModel(
                            field.Header,
                            displayText,
                            mapping.Item.FieldErrors.TryGetValue(field.FieldId, out var fieldError)
                                ? fieldError
                                : hasMappedValue
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
                severity = mapping.Item.FieldErrors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
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
            exclusionDebugRules) { SampleItem = mapping.Item };
    }

    internal static string GetDirectoryLeafName(string canonicalPath) =>
        ProfileSampleEvaluator.GetDirectoryLeafName(canonicalPath);

    internal static ProfileDirectoryExclusionTestResultViewModel
        BuildDirectoryExclusionTestResult(
            string directoryName,
            int ruleCount,
            ILoadedProfile? profile,
            IAppLocalizer? localizer = null,
            ProfileDirectoryNameExclusionResult? evaluationResult = null)
    {
        var scanScope = Get(
            localizer,
            "Loc.Playground.Exclusion.Scope",
            "이 판정은 구조화 보기의 스캔에서 검색 루트 아래 폴더의 마지막 이름(leaf)에 적용됩니다. "
            + "일치한 폴더와 그 하위 폴더는 인덱싱하지 않습니다. 검색 루트 자체는 예외 규칙으로 제외하지 않습니다. "
            + "기존 DB 불러오기는 저장된 인덱스를 사용하므로 새 규칙을 반영하려면 다시 스캔하세요.");

        if (string.IsNullOrEmpty(directoryName))
        {
            return new ProfileDirectoryExclusionTestResultViewModel(
                Get(localizer, "Loc.Playground.Exclusion.Root", "(드라이브 또는 공유 루트)"),
                Get(
                    localizer,
                    "Loc.Playground.Exclusion.NoLeaf",
                    "leaf 폴더 이름이 없어 제외 판정을 생략했습니다."),
                scanScope,
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
                scanScope,
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
                + scanScope,
                InfoBarSeverity.Error);
        }

        var evaluation = evaluationResult ?? profile.EvaluateDirectoryName(directoryName);
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
                    "같은 부모의 다음 폴더 탐색은 계속합니다. ") + scanScope,
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
                + scanScope,
                InfoBarSeverity.Warning);
        }

        return new ProfileDirectoryExclusionTestResultViewModel(
            directoryName,
            Get(
                localizer,
                "Loc.Playground.Exclusion.NoMatch",
                "어떤 제외 규칙에도 일치하지 않아 이 폴더를 계속 탐색합니다."),
            scanScope,
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
