using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public sealed class ProfileRegexDebugRuleViewModel
{
    public ProfileRegexDebugRuleViewModel(
        string ruleId,
        string pattern,
        string matchMode,
        string status,
        Brush accentBrush,
        IReadOnlyList<ProfileRegexDebugLaneViewModel> lanes)
    {
        RuleId = ruleId;
        Pattern = pattern;
        MatchMode = matchMode;
        Status = status;
        AccentBrush = accentBrush;
        Lanes = lanes;
    }

    public string RuleId { get; }

    public string Pattern { get; }

    public string MatchMode { get; }

    public string Status { get; }

    public Brush AccentBrush { get; }

    public IReadOnlyList<ProfileRegexDebugLaneViewModel> Lanes { get; }
}

public sealed class ProfileRegexDebugLaneViewModel
{
    public ProfileRegexDebugLaneViewModel(
        string label,
        string detail,
        Brush markerBrush,
        IReadOnlyList<ProfileRegexDebugSegmentViewModel> segments)
    {
        Label = label;
        Detail = detail;
        MarkerBrush = markerBrush;
        Segments = segments;
    }

    public string Label { get; }

    public string Detail { get; }

    public Brush MarkerBrush { get; }

    public IReadOnlyList<ProfileRegexDebugSegmentViewModel> Segments { get; }
}

public sealed class ProfileRegexDebugSegmentViewModel
{
    public ProfileRegexDebugSegmentViewModel(
        string text,
        bool isHighlighted,
        Brush backgroundBrush,
        string toolTip)
    {
        Text = text;
        IsHighlighted = isHighlighted;
        BackgroundBrush = backgroundBrush;
        ToolTip = toolTip;
    }

    public string Text { get; }

    public bool IsHighlighted { get; }

    public Brush BackgroundBrush { get; }

    public string ToolTip { get; }
}

/// <summary>
/// Builds a safe, read-only visualization of the user's original expressions.
/// Full-mode anchors are deliberately not added here: the editor needs to show
/// useful substring hits even when the complete profile rule does not yet match.
/// </summary>
internal static class ProfileRegexDebugBuilder
{
    private const int MaximumDisplayedRuleCount = 32;
    private const int MaximumDisplayedMatchCount = 256;
    private const int MaximumDisplayedCaptureCount = 256;
    private const int MaximumDisplayedNamedGroupCount = 64;
    private static readonly TimeSpan MaximumPreviewDuration = TimeSpan.FromSeconds(2);

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0x10, 0xB9, 0x81),
        Color.FromRgb(0x8B, 0x5C, 0xF6),
        Color.FromRgb(0xEC, 0x48, 0x99),
        Color.FromRgb(0x06, 0xB6, 0xD4),
        Color.FromRgb(0xEF, 0x44, 0x44),
        Color.FromRgb(0x84, 0xCC, 0x16),
    ];

    private static readonly Brush TransparentBrush = Freeze(Colors.Transparent, 0);

    public static IReadOnlyList<ProfileRegexDebugRuleViewModel> Build(
        string input,
        IReadOnlyList<ProfileRegexRuleManifest> rules) =>
        Build(
            input,
            rules,
            MaximumPreviewDuration,
            MaximumDisplayedRuleCount,
            RegexDebugSubject.AbsolutePath);

    /// <summary>
    /// Builds the same raw-pattern and named-group visualization for directory
    /// exclusion rules. The input must be one canonical path's leaf name, which
    /// is the exact value supplied to the runtime exclusion evaluator.
    /// </summary>
    public static IReadOnlyList<ProfileRegexDebugRuleViewModel> BuildDirectoryName(
        string directoryName,
        IReadOnlyList<ProfileDirectoryNameExclusionRuleManifest> rules) =>
        Build(
            directoryName,
            rules.Select(static rule => new ProfileRegexRuleManifest
            {
                Id = rule.Id,
                Pattern = rule.Pattern,
                MatchMode = rule.MatchMode,
                IgnoreCase = rule.IgnoreCase,
                TimeoutMilliseconds = rule.TimeoutMilliseconds,
            }).ToArray(),
            MaximumPreviewDuration,
            MaximumDisplayedRuleCount,
            RegexDebugSubject.DirectoryName);

    internal static IReadOnlyList<ProfileRegexDebugRuleViewModel> Build(
        string input,
        IReadOnlyList<ProfileRegexRuleManifest> rules,
        TimeSpan previewDuration,
        int maximumDisplayedRuleCount)
        => Build(
            input,
            rules,
            previewDuration,
            maximumDisplayedRuleCount,
            RegexDebugSubject.AbsolutePath);

    internal static IReadOnlyList<ProfileRegexDebugRuleViewModel> BuildDirectoryName(
        string directoryName,
        IReadOnlyList<ProfileDirectoryNameExclusionRuleManifest> rules,
        TimeSpan previewDuration,
        int maximumDisplayedRuleCount) =>
        Build(
            directoryName,
            rules.Select(static rule => new ProfileRegexRuleManifest
            {
                Id = rule.Id,
                Pattern = rule.Pattern,
                MatchMode = rule.MatchMode,
                IgnoreCase = rule.IgnoreCase,
                TimeoutMilliseconds = rule.TimeoutMilliseconds,
            }).ToArray(),
            previewDuration,
            maximumDisplayedRuleCount,
            RegexDebugSubject.DirectoryName);

    private static IReadOnlyList<ProfileRegexDebugRuleViewModel> Build(
        string input,
        IReadOnlyList<ProfileRegexRuleManifest> rules,
        TimeSpan previewDuration,
        int maximumDisplayedRuleCount,
        RegexDebugSubject subject)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        if (previewDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previewDuration),
                previewDuration,
                "The preview duration must be positive.");
        }

        if (maximumDisplayedRuleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDisplayedRuleCount),
                maximumDisplayedRuleCount,
                "At least one rule must be available for preview.");
        }

        var budget = new PreviewBudget(previewDuration);
        var displayedRuleCount = Math.Min(rules.Count, maximumDisplayedRuleCount);
        var result = new List<ProfileRegexDebugRuleViewModel>(
            Math.Min(displayedRuleCount + 1, rules.Count + 1));
        for (var ruleIndex = 0; ruleIndex < displayedRuleCount; ruleIndex++)
        {
            if (!budget.HasTimeRemaining)
            {
                result.Add(CreateLimitNotice(
                    ProfileEditorText.Format(
                        "Loc.RegexDebug.Limit.Time",
                        "색상 미리보기의 전체 시간 제한 {0:0.##}초에 도달해 나머지 {1:N0}개 규칙은 표시하지 않았습니다. 이 제한은 색상 표시 전용이며 프로필 검증 대상을 줄이지 않습니다.",
                        previewDuration.TotalSeconds,
                        rules.Count - ruleIndex)));
                return result;
            }

            var rule = BuildRule(
                input,
                rules[ruleIndex],
                ruleIndex,
                subject,
                budget,
                out var budgetReached);
            result.Add(rule);
            if (budgetReached && ruleIndex + 1 < rules.Count)
            {
                result.Add(CreateLimitNotice(
                    ProfileEditorText.Format(
                        "Loc.RegexDebug.Limit.Time",
                        "색상 미리보기의 전체 시간 제한 {0:0.##}초에 도달해 나머지 {1:N0}개 규칙은 표시하지 않았습니다. 이 제한은 색상 표시 전용이며 프로필 검증 대상을 줄이지 않습니다.",
                        previewDuration.TotalSeconds,
                        rules.Count - ruleIndex - 1)));
                return result;
            }
        }

        if (rules.Count > displayedRuleCount)
        {
            result.Add(CreateLimitNotice(
                ProfileEditorText.Format(
                    "Loc.RegexDebug.Limit.Count",
                    "규칙이 많아 앞 {0:N0}개만 색상으로 표시했습니다. 나머지 {1:N0}개 규칙도 프로필 검증 대상에는 그대로 포함됩니다.",
                    displayedRuleCount,
                    rules.Count - displayedRuleCount)));
        }

        return result;
    }

    private static ProfileRegexDebugRuleViewModel BuildRule(
        string input,
        ProfileRegexRuleManifest rule,
        int ruleIndex,
        RegexDebugSubject subject,
        PreviewBudget budget,
        out bool budgetReached)
    {
        budgetReached = false;
        var ruleId = string.IsNullOrWhiteSpace(rule.Id)
            ? $"rule-{ruleIndex + 1}"
            : rule.Id.Trim();
        var pattern = rule.Pattern ?? string.Empty;
        var accent = Palette[ruleIndex % Palette.Length];
        var accentBrush = Freeze(accent, byte.MaxValue);
        var matchMode = rule.MatchMode == ProfileRegexMatchMode.Full
            ? subject == RegexDebugSubject.DirectoryName
                ? ProfileEditorText.Get("Loc.RegexDebug.Mode.FolderFull", "폴더 이름 전체 일치")
                : ProfileEditorText.Get("Loc.RegexDebug.Mode.PathFull", "전체 경로 일치 규칙")
            : ProfileEditorText.Get("Loc.RegexDebug.Mode.Partial", "부분 일치 규칙");
        var maximumPatternLength = subject == RegexDebugSubject.DirectoryName
            ? ProfileManifestLimits.MaximumExcludedDirectoryNamePatternLength
            : ProfileManifestLimits.MaximumRegexPatternLength;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                ProfileEditorText.Get("Loc.RegexDebug.Error.Empty", "패턴이 비어 있습니다."),
                accentBrush,
                accent);
        }

        if (pattern.Length > maximumPatternLength)
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                ProfileEditorText.Format(
                    "Loc.RegexDebug.Error.TooLong",
                    "패턴은 최대 {0:N0}자까지 사용할 수 있습니다.",
                    maximumPatternLength),
                accentBrush,
                accent);
        }

        if (rule.TimeoutMilliseconds is < 1 or > 10_000)
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                ProfileEditorText.Get(
                    "Loc.RegexDebug.Error.TimeoutRange",
                    "시간 제한은 1~10000ms여야 합니다."),
                accentBrush,
                accent);
        }

        try
        {
            var options = RegexOptions.CultureInvariant;
            if (rule.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            if (!budget.TryGetMatchTimeout(
                    TimeSpan.FromMilliseconds(rule.TimeoutMilliseconds),
                    out var rawMatchTimeout,
                    out var rawTimeoutLimitedByBudget))
            {
                budgetReached = true;
                return ErrorRule(
                    input,
                    ruleId,
                    pattern,
                    matchMode,
                    ProfileEditorText.Get(
                        "Loc.RegexDebug.Error.Budget",
                        "색상 미리보기의 전체 시간 제한에 도달해 이 규칙을 검사하지 못했습니다."),
                    accentBrush,
                    accent);
            }

            var regex = new Regex(
                pattern,
                options,
                rawMatchTimeout);
            var matches = CollectMatches(
                regex,
                input,
                out var matchesTruncated,
                out var partialEvaluationTimedOut);
            budgetReached = budget.IsExpired
                || (partialEvaluationTimedOut && rawTimeoutLimitedByBudget);
            var positiveMatches = matches
                .Where(static match => match.Success && match.Length > 0)
                .ToArray();
            var actualRuleMatches = matches.Length > 0;
            var fullEvaluationTimedOut = false;
            var fullEvaluationSkippedByBudget = false;
            if (rule.MatchMode == ProfileRegexMatchMode.Full)
            {
                if (!budget.TryGetMatchTimeout(
                        TimeSpan.FromMilliseconds(rule.TimeoutMilliseconds),
                        out var fullMatchTimeout,
                        out var fullTimeoutLimitedByBudget))
                {
                    actualRuleMatches = false;
                    budgetReached = true;
                    fullEvaluationSkippedByBudget = true;
                }
                else
                {
                    try
                    {
                        actualRuleMatches = new Regex(
                            $"\\A(?:{pattern})\\z",
                            options,
                            fullMatchTimeout).IsMatch(input);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // The raw matches are still valuable debugging evidence. Keep
                        // their colored lanes and report only the anchored check failure.
                        actualRuleMatches = false;
                        fullEvaluationTimedOut = true;
                        budgetReached |= fullTimeoutLimitedByBudget;
                    }
                }
            }
            var overallRanges = positiveMatches
                .Select(static match => new HighlightRange(match.Index, match.Length))
                .ToArray();
            var lanes = new List<ProfileRegexDebugLaneViewModel>
            {
                new(
                    ProfileEditorText.Get("Loc.RegexDebug.Pattern", "패턴"),
                    BuildPatternDetail(
                        matches,
                        positiveMatches,
                        matchesTruncated,
                        partialEvaluationTimedOut),
                    accentBrush,
                    BuildSegments(
                        input,
                        overallRanges,
                        Freeze(accent, 82),
                        ProfileEditorText.Format(
                            "Loc.RegexDebug.Tooltip.PatternMatch",
                            "규칙 '{0}'의 원본 패턴 일치",
                            ruleId))),
            };

            var allNamedGroups = regex.GetGroupNames()
                .Where(static name => !int.TryParse(
                    name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
                .ToArray();
            var namedGroups = allNamedGroups
                .Take(MaximumDisplayedNamedGroupCount)
                .ToArray();
            var displayedNamedGroupCount = 0;
            for (var groupIndex = 0; groupIndex < namedGroups.Length; groupIndex++)
            {
                if (!budget.HasTimeRemaining)
                {
                    budgetReached = true;
                    break;
                }

                var groupName = namedGroups[groupIndex];
                var sampledRanges = matches
                    .SelectMany(match => match.Groups[groupName].Captures.Cast<Capture>())
                    .Where(static capture => capture.Length > 0)
                    .Select(static capture => new HighlightRange(capture.Index, capture.Length))
                    .Take(MaximumDisplayedCaptureCount + 1)
                    .ToArray();
                var capturesTruncated = sampledRanges.Length > MaximumDisplayedCaptureCount;
                var ranges = sampledRanges
                    .Take(MaximumDisplayedCaptureCount)
                    .ToArray();
                var groupColor = Palette[(ruleIndex + groupIndex + 1) % Palette.Length];
                var groupBrush = Freeze(groupColor, byte.MaxValue);
                lanes.Add(new ProfileRegexDebugLaneViewModel(
                    groupName,
                    ranges.Length == 0
                        ? ProfileEditorText.Get("Loc.RegexDebug.Capture.None", "캡처되지 않음")
                        : capturesTruncated
                            ? ProfileEditorText.Format(
                                "Loc.RegexDebug.Capture.Truncated",
                                "캡처 {0:N0}개까지 표시",
                                ranges.Length)
                            : ProfileEditorText.Format(
                                "Loc.RegexDebug.Capture.Count",
                                "캡처 {0:N0}개",
                                ranges.Length),
                    groupBrush,
                    BuildSegments(
                        input,
                        ranges,
                        Freeze(groupColor, 82),
                        ProfileEditorText.Format(
                            "Loc.RegexDebug.Tooltip.NamedCapture",
                            "named group '{0}' 캡처",
                            groupName))));
                displayedNamedGroupCount++;
            }

            return new ProfileRegexDebugRuleViewModel(
                ruleId,
                pattern,
                matchMode,
                BuildStatus(
                    subject,
                    rule.MatchMode,
                    actualRuleMatches,
                    matches,
                    positiveMatches,
                    matchesTruncated,
                    allNamedGroups.Length > displayedNamedGroupCount,
                    partialEvaluationTimedOut,
                    fullEvaluationTimedOut,
                    fullEvaluationSkippedByBudget,
                    budgetReached || budget.IsExpired),
                accentBrush,
                lanes);
        }
        catch (RegexMatchTimeoutException)
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                ProfileEditorText.Format(
                    "Loc.RegexDebug.Error.RuleTimeout",
                    "{0:N0}ms 안에 검사를 마치지 못했습니다.",
                    rule.TimeoutMilliseconds),
                accentBrush,
                accent);
        }
        catch (ArgumentException exception)
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                ProfileEditorText.Format(
                    "Loc.RegexDebug.Error.Regex",
                    "정규식 오류: {0}",
                    exception.Message),
                accentBrush,
                accent);
        }
    }

    private static ProfileRegexDebugRuleViewModel CreateLimitNotice(string status)
    {
        var color = Color.FromRgb(0x60, 0x60, 0x60);
        return new ProfileRegexDebugRuleViewModel(
            ProfileEditorText.Get("Loc.RegexDebug.Limit.Title", "미리보기 제한"),
            string.Empty,
            ProfileEditorText.Get("Loc.RegexDebug.Limit.Mode", "색상 디버깅"),
            status,
            Freeze(color, byte.MaxValue),
            []);
    }

    private static ProfileRegexDebugRuleViewModel ErrorRule(
        string input,
        string ruleId,
        string pattern,
        string matchMode,
        string status,
        Brush accentBrush,
        Color accent)
    {
        var lane = new ProfileRegexDebugLaneViewModel(
            ProfileEditorText.Get("Loc.RegexDebug.Pattern", "패턴"),
            ProfileEditorText.Get("Loc.RegexDebug.Highlight.Unavailable", "강조할 수 없음"),
            accentBrush,
            BuildSegments(
                input,
                [],
                Freeze(accent, 82),
                ProfileEditorText.Format(
                    "Loc.RegexDebug.Tooltip.PatternMatch",
                    "규칙 '{0}'의 원본 패턴 일치",
                    ruleId)));
        return new ProfileRegexDebugRuleViewModel(
            ruleId,
            pattern,
            matchMode,
            status,
            accentBrush,
            [lane]);
    }

    private static string BuildPatternDetail(
        IReadOnlyCollection<Match> matches,
        IReadOnlyCollection<Match> positiveMatches,
        bool matchesTruncated,
        bool evaluationTimedOut)
    {
        var timeoutNotice = evaluationTimedOut
            ? ProfileEditorText.Get(
                "Loc.RegexDebug.Detail.BeforeTimeout",
                " · 시간 초과 전 결과")
            : string.Empty;
        if (positiveMatches.Count > 0)
        {
            return matchesTruncated
                ? ProfileEditorText.Format(
                    "Loc.RegexDebug.Detail.MatchTruncated",
                    "원본 패턴 일치 {0:N0}개까지 표시{1}",
                    positiveMatches.Count,
                    timeoutNotice)
                : ProfileEditorText.Format(
                    "Loc.RegexDebug.Detail.MatchCount",
                    "원본 패턴 일치 {0:N0}개{1}",
                    positiveMatches.Count,
                    timeoutNotice);
        }

        return matches.Count > 0
            ? matchesTruncated
                ? ProfileEditorText.Format(
                    "Loc.RegexDebug.Detail.ZeroTruncated",
                    "길이 0인 일치 {0:N0}개까지 표시{1}",
                    matches.Count,
                    timeoutNotice)
                : ProfileEditorText.Format(
                    "Loc.RegexDebug.Detail.ZeroCount",
                    "길이 0인 일치 {0:N0}개{1}",
                    matches.Count,
                    timeoutNotice)
            : evaluationTimedOut
                ? ProfileEditorText.Get(
                    "Loc.RegexDebug.Detail.PartialTimeout",
                    "부분 검사 시간 초과")
                : ProfileEditorText.Get("Loc.RegexDebug.Detail.NoMatch", "일치 없음");
    }

    private static string BuildStatus(
        RegexDebugSubject subject,
        ProfileRegexMatchMode matchMode,
        bool actualRuleMatches,
        IReadOnlyCollection<Match> matches,
        IReadOnlyCollection<Match> positiveMatches,
        bool matchesTruncated,
        bool namedGroupsTruncated,
        bool partialEvaluationTimedOut,
        bool fullEvaluationTimedOut,
        bool fullEvaluationSkippedByBudget,
        bool previewBudgetReached)
    {
        var notices = new List<string>(4);
        if (matchesTruncated || namedGroupsTruncated)
        {
            notices.Add(ProfileEditorText.Get(
                "Loc.RegexDebug.Notice.Truncated",
                "안전을 위해 일부 결과만 표시"));
        }

        if (partialEvaluationTimedOut)
        {
            notices.Add(ProfileEditorText.Get(
                "Loc.RegexDebug.Notice.PartialTimeout",
                "원본 패턴 부분 검사가 시간 초과되어 그전 결과만 표시"));
        }

        if (fullEvaluationTimedOut)
        {
            notices.Add(ProfileEditorText.Get(
                "Loc.RegexDebug.Notice.FullTimeout",
                "실제 전체 일치 확인 시간 초과"));
        }

        if (fullEvaluationSkippedByBudget)
        {
            notices.Add(ProfileEditorText.Get(
                "Loc.RegexDebug.Notice.FullSkipped",
                "실제 전체 일치 확인 생략"));
        }

        if (previewBudgetReached)
        {
            notices.Add(ProfileEditorText.Get(
                "Loc.RegexDebug.Notice.Budget",
                "색상 미리보기 전체 시간 제한 도달"));
        }

        var notice = notices.Count == 0
            ? string.Empty
            : " · " + string.Join(" · ", notices);
        if (positiveMatches.Count == 0)
        {
            return matches.Count > 0
                ? ProfileEditorText.Format(
                    "Loc.RegexDebug.Status.ZeroOnly",
                    "원본 패턴이 길이 0인 위치에만 일치합니다. 색으로 표시할 문자는 없습니다.{0}",
                    notice)
                : ProfileEditorText.Format(
                    "Loc.RegexDebug.Status.NoPartial",
                    "원본 패턴과 부분적으로 일치하는 구간이 없습니다.{0}",
                    notice);
        }

        if (matchMode == ProfileRegexMatchMode.Full)
        {
            var fullMatchDescription = subject == RegexDebugSubject.DirectoryName
                ? ProfileEditorText.Get(
                    "Loc.RegexDebug.Status.ActualFolderFull",
                    "실제 폴더 이름 전체 일치")
                : ProfileEditorText.Get(
                    "Loc.RegexDebug.Status.ActualPathFull",
                    "실제 전체 경로 일치");
            var requirementDescription = subject == RegexDebugSubject.DirectoryName
                ? ProfileEditorText.Get(
                    "Loc.RegexDebug.Status.FolderRequirement",
                    "실제 바로 스캔에서는 폴더 이름 전체가 일치해야 합니다.")
                : ProfileEditorText.Get(
                    "Loc.RegexDebug.Status.PathRequirement",
                    "실제 스캔에서는 전체 경로가 일치해야 합니다.");
            return actualRuleMatches
                ? ProfileEditorText.Format(
                    "Loc.RegexDebug.Status.FullMatched",
                    "{0} · 원본 패턴 후보 {1:N0}개{2}",
                    fullMatchDescription,
                    positiveMatches.Count,
                    notice)
                : fullEvaluationTimedOut || fullEvaluationSkippedByBudget
                    ? ProfileEditorText.Format(
                        "Loc.RegexDebug.Status.PartialCandidates",
                        "부분 후보 {0:N0}개{1}",
                        positiveMatches.Count,
                        notice)
                    : ProfileEditorText.Format(
                        "Loc.RegexDebug.Status.PartialRequirement",
                        "부분 후보 {0:N0}개 · {1}{2}",
                        positiveMatches.Count,
                        requirementDescription,
                        notice);
        }

        return ProfileEditorText.Format(
            "Loc.RegexDebug.Status.PartialMatched",
            "부분 일치 {0:N0}개{1}",
            positiveMatches.Count,
            notice);
    }

    private static Match[] CollectMatches(
        Regex regex,
        string input,
        out bool truncated,
        out bool timedOut)
    {
        var matches = new List<Match>(MaximumDisplayedMatchCount + 1);
        timedOut = false;
        try
        {
            var current = regex.Match(input);
            while (current.Success && matches.Count <= MaximumDisplayedMatchCount)
            {
                matches.Add(current);
                if (matches.Count > MaximumDisplayedMatchCount)
                {
                    break;
                }

                current = current.NextMatch();
            }
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
        }

        truncated = matches.Count > MaximumDisplayedMatchCount;
        return matches.Take(MaximumDisplayedMatchCount).ToArray();
    }

    private static IReadOnlyList<ProfileRegexDebugSegmentViewModel> BuildSegments(
        string input,
        IReadOnlyCollection<HighlightRange> ranges,
        Brush highlightBrush,
        string highlightedToolTip)
    {
        if (input.Length == 0)
        {
            return [];
        }

        var normalized = ranges
            .Select(range => new HighlightRange(
                Math.Clamp(range.Start, 0, input.Length),
                Math.Clamp(range.Length, 0, input.Length - Math.Clamp(range.Start, 0, input.Length))))
            .Where(static range => range.Length > 0)
            .ToArray();
        var boundaries = normalized
            .SelectMany(static range => new[] { range.Start, range.End })
            .Append(0)
            .Append(input.Length)
            .Distinct()
            .Order()
            .ToArray();
        var segments = new List<ProfileRegexDebugSegmentViewModel>();
        for (var index = 0; index < boundaries.Length - 1; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            if (end <= start)
            {
                continue;
            }

            var isHighlighted = normalized.Any(range =>
                range.Start <= start && range.End >= end);
            var text = input[start..end];
            var toolTip = isHighlighted
                ? ProfileEditorText.Format(
                    "Loc.RegexDebug.Tooltip.Position",
                    "{0} · 위치 {1:N0}~{2:N0}",
                    highlightedToolTip,
                    start,
                    end - 1)
                : ProfileEditorText.Get(
                    "Loc.RegexDebug.Tooltip.Unmatched",
                    "일치하지 않는 구간");
            if (segments.Count > 0
                && segments[^1].IsHighlighted == isHighlighted
                && string.Equals(segments[^1].ToolTip, toolTip, StringComparison.Ordinal))
            {
                var previous = segments[^1];
                segments[^1] = new ProfileRegexDebugSegmentViewModel(
                    previous.Text + text,
                    isHighlighted,
                    previous.BackgroundBrush,
                    previous.ToolTip);
                continue;
            }

            segments.Add(new ProfileRegexDebugSegmentViewModel(
                text,
                isHighlighted,
                isHighlighted ? highlightBrush : TransparentBrush,
                toolTip));
        }

        return segments;
    }

    private static Brush Freeze(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private sealed class PreviewBudget
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly TimeSpan _duration;

        public PreviewBudget(TimeSpan duration) => _duration = duration;

        public bool HasTimeRemaining => _stopwatch.Elapsed < _duration;

        public bool IsExpired => !HasTimeRemaining;

        public bool TryGetMatchTimeout(
            TimeSpan configuredTimeout,
            out TimeSpan effectiveTimeout,
            out bool limitedByBudget)
        {
            var remaining = _duration - _stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                effectiveTimeout = default;
                limitedByBudget = true;
                return false;
            }

            limitedByBudget = remaining < configuredTimeout;
            effectiveTimeout = limitedByBudget ? remaining : configuredTimeout;
            return true;
        }
    }

    private enum RegexDebugSubject
    {
        AbsolutePath,
        DirectoryName,
    }

    private readonly record struct HighlightRange(int Start, int Length)
    {
        public int End => checked(Start + Length);
    }
}
