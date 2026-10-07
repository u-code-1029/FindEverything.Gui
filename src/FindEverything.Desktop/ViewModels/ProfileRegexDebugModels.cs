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
        Build(input, rules, MaximumPreviewDuration, MaximumDisplayedRuleCount);

    internal static IReadOnlyList<ProfileRegexDebugRuleViewModel> Build(
        string input,
        IReadOnlyList<ProfileRegexRuleManifest> rules,
        TimeSpan previewDuration,
        int maximumDisplayedRuleCount)
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
                    $"색상 미리보기의 전체 시간 제한 {previewDuration.TotalSeconds:0.##}초에 도달해 "
                    + $"나머지 {rules.Count - ruleIndex:N0}개 규칙은 표시하지 않았습니다. "
                    + "이 제한은 색상 표시 전용이며 프로필 검증 대상을 줄이지 않습니다."));
                return result;
            }

            var rule = BuildRule(input, rules[ruleIndex], ruleIndex, budget, out var budgetReached);
            result.Add(rule);
            if (budgetReached && ruleIndex + 1 < rules.Count)
            {
                result.Add(CreateLimitNotice(
                    $"색상 미리보기의 전체 시간 제한 {previewDuration.TotalSeconds:0.##}초에 도달해 "
                    + $"나머지 {rules.Count - ruleIndex - 1:N0}개 규칙은 표시하지 않았습니다. "
                    + "이 제한은 색상 표시 전용이며 프로필 검증 대상을 줄이지 않습니다."));
                return result;
            }
        }

        if (rules.Count > displayedRuleCount)
        {
            result.Add(CreateLimitNotice(
                $"규칙이 많아 앞 {displayedRuleCount:N0}개만 색상으로 표시했습니다. "
                + $"나머지 {rules.Count - displayedRuleCount:N0}개 규칙도 프로필 검증 대상에는 그대로 포함됩니다."));
        }

        return result;
    }

    private static ProfileRegexDebugRuleViewModel BuildRule(
        string input,
        ProfileRegexRuleManifest rule,
        int ruleIndex,
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
            ? "전체 경로 일치 규칙"
            : "부분 일치 규칙";

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                "패턴이 비어 있습니다.",
                accentBrush,
                accent);
        }

        if (pattern.Length > ProfileManifestLimits.MaximumRegexPatternLength)
        {
            return ErrorRule(
                input,
                ruleId,
                pattern,
                matchMode,
                $"패턴은 최대 {ProfileManifestLimits.MaximumRegexPatternLength:N0}자까지 사용할 수 있습니다.",
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
                "시간 제한은 1~10000ms여야 합니다.",
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
                    "색상 미리보기의 전체 시간 제한에 도달해 이 규칙을 검사하지 못했습니다.",
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
                    "패턴",
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
                        $"규칙 '{ruleId}'의 원본 패턴 일치")),
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
                        ? "캡처되지 않음"
                        : capturesTruncated
                            ? $"캡처 {ranges.Length:N0}개까지 표시"
                            : $"캡처 {ranges.Length:N0}개",
                    groupBrush,
                    BuildSegments(
                        input,
                        ranges,
                        Freeze(groupColor, 82),
                        $"named group '{groupName}' 캡처")));
                displayedNamedGroupCount++;
            }

            return new ProfileRegexDebugRuleViewModel(
                ruleId,
                pattern,
                matchMode,
                BuildStatus(
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
                $"{rule.TimeoutMilliseconds:N0}ms 안에 검사를 마치지 못했습니다.",
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
                $"정규식 오류: {exception.Message}",
                accentBrush,
                accent);
        }
    }

    private static ProfileRegexDebugRuleViewModel CreateLimitNotice(string status)
    {
        var color = Color.FromRgb(0x60, 0x60, 0x60);
        return new ProfileRegexDebugRuleViewModel(
            "미리보기 제한",
            string.Empty,
            "색상 디버깅",
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
            "패턴",
            "강조할 수 없음",
            accentBrush,
            BuildSegments(
                input,
                [],
                Freeze(accent, 82),
                $"규칙 '{ruleId}'의 원본 패턴 일치"));
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
        var timeoutNotice = evaluationTimedOut ? " · 시간 초과 전 결과" : string.Empty;
        if (positiveMatches.Count > 0)
        {
            return matchesTruncated
                ? $"원본 패턴 일치 {positiveMatches.Count:N0}개까지 표시{timeoutNotice}"
                : $"원본 패턴 일치 {positiveMatches.Count:N0}개{timeoutNotice}";
        }

        return matches.Count > 0
            ? matchesTruncated
                ? $"길이 0인 일치 {matches.Count:N0}개까지 표시{timeoutNotice}"
                : $"길이 0인 일치 {matches.Count:N0}개{timeoutNotice}"
            : evaluationTimedOut
                ? "부분 검사 시간 초과"
                : "일치 없음";
    }

    private static string BuildStatus(
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
            notices.Add("안전을 위해 일부 결과만 표시");
        }

        if (partialEvaluationTimedOut)
        {
            notices.Add("원본 패턴 부분 검사가 시간 초과되어 그전 결과만 표시");
        }

        if (fullEvaluationTimedOut)
        {
            notices.Add("실제 전체 일치 확인 시간 초과");
        }

        if (fullEvaluationSkippedByBudget)
        {
            notices.Add("실제 전체 일치 확인 생략");
        }

        if (previewBudgetReached)
        {
            notices.Add("색상 미리보기 전체 시간 제한 도달");
        }

        var notice = notices.Count == 0
            ? string.Empty
            : " · " + string.Join(" · ", notices);
        if (positiveMatches.Count == 0)
        {
            return matches.Count > 0
                ? $"원본 패턴이 길이 0인 위치에만 일치합니다. 색으로 표시할 문자는 없습니다.{notice}"
                : $"원본 패턴과 부분적으로 일치하는 구간이 없습니다.{notice}";
        }

        if (matchMode == ProfileRegexMatchMode.Full)
        {
            return actualRuleMatches
                ? $"실제 전체 경로 일치 · 원본 패턴 후보 {positiveMatches.Count:N0}개{notice}"
                : fullEvaluationTimedOut || fullEvaluationSkippedByBudget
                    ? $"부분 후보 {positiveMatches.Count:N0}개{notice}"
                    : $"부분 후보 {positiveMatches.Count:N0}개 · 실제 스캔에서는 전체 경로가 일치해야 합니다.{notice}";
        }

        return $"부분 일치 {positiveMatches.Count:N0}개{notice}";
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
                ? $"{highlightedToolTip} · 위치 {start:N0}~{end - 1:N0}"
                : "일치하지 않는 구간";
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

    private readonly record struct HighlightRange(int Start, int Length)
    {
        public int End => checked(Start + Length);
    }
}
