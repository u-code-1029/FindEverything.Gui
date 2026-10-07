using System.IO;
using System.Windows.Media;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileRegexDebugBuilderTests
{
    [Fact]
    public void Full_rule_still_highlights_a_raw_partial_match()
    {
        var rows = ProfileRegexDebugBuilder.Build(
            @"C:\Archive\ABC-123\Tail",
            [Rule("project", @"(?<code>ABC-\d+)", ProfileRegexMatchMode.Full)]);

        var row = Assert.Single(rows);
        Assert.Contains("부분 후보", row.Status, StringComparison.Ordinal);
        Assert.Equal(2, row.Lanes.Count);
        Assert.Equal("패턴", row.Lanes[0].Label);
        Assert.Equal("code", row.Lanes[1].Label);
        Assert.Equal("ABC-123", HighlightedText(row.Lanes[0]));
        Assert.Equal("ABC-123", HighlightedText(row.Lanes[1]));
    }

    [Fact]
    public void Named_groups_have_separate_lanes_and_colors_even_when_nested()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            @"C:\Archive\2026-0521",
            [Rule(
                "date",
                @"(?<date>(?<year>\d{4})-(?<monthDay>\d{4}))",
                ProfileRegexMatchMode.Partial)]));

        Assert.Equal(["패턴", "date", "year", "monthDay"], row.Lanes.Select(static lane => lane.Label));
        Assert.Equal("2026-0521", HighlightedText(row.Lanes[1]));
        Assert.Equal("2026", HighlightedText(row.Lanes[2]));
        Assert.Equal("0521", HighlightedText(row.Lanes[3]));
        Assert.NotEqual(
            BrushColor(row.Lanes[1].MarkerBrush),
            BrushColor(row.Lanes[2].MarkerBrush));
        Assert.NotEqual(
            BrushColor(row.Lanes[2].MarkerBrush),
            BrushColor(row.Lanes[3].MarkerBrush));
    }

    [Fact]
    public void Multiple_matches_are_all_highlighted()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            @"C:\2024\Archive\2026",
            [Rule("years", @"(?<year>20\d{2})", ProfileRegexMatchMode.Partial)]));

        Assert.Contains("2개", row.Status, StringComparison.Ordinal);
        Assert.Equal("20242026", HighlightedText(row.Lanes[0]));
        Assert.Equal("20242026", HighlightedText(row.Lanes[1]));
    }

    [Fact]
    public void Full_rule_is_evaluated_with_runtime_anchors_not_the_first_raw_alternative()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            "ABC",
            [Rule("alternative", "A|ABC", ProfileRegexMatchMode.Full)]));

        Assert.Contains("실제 전체 경로 일치", row.Status, StringComparison.Ordinal);
        Assert.Equal("A", HighlightedText(row.Lanes[0]));
    }

    [Fact]
    public void Preview_caps_high_volume_matches_before_building_segments()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            new string('a', 400),
            [Rule("many", "(?<letter>a)", ProfileRegexMatchMode.Partial)]));

        Assert.Contains("일부 결과만 표시", row.Status, StringComparison.Ordinal);
        Assert.Equal(256, HighlightedText(row.Lanes[1]).Length);
    }

    [Fact]
    public void Preview_does_not_search_for_another_match_after_the_truncation_sentinel()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            new string('a', 257) + new string('b', 100_000) + "!",
            [Rule(
                "sentinel",
                "a|(b+)+$",
                ProfileRegexMatchMode.Partial,
                timeoutMilliseconds: 100)]));

        Assert.Contains("일부 결과만 표시", row.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("시간 초과", row.Status, StringComparison.Ordinal);
        Assert.Equal(256, HighlightedText(row.Lanes[0]).Length);
    }

    [Fact]
    public void Preview_caps_the_number_of_colored_rule_cards()
    {
        var rows = ProfileRegexDebugBuilder.Build(
            "ABC",
            Enumerable.Range(1, 5)
                .Select(index => Rule(
                    $"rule-{index}",
                    "ABC",
                    ProfileRegexMatchMode.Partial))
                .ToArray(),
            TimeSpan.FromSeconds(1),
            maximumDisplayedRuleCount: 2);

        Assert.Equal(3, rows.Count);
        Assert.Equal("rule-1", rows[0].RuleId);
        Assert.Equal("rule-2", rows[1].RuleId);
        Assert.Equal("미리보기 제한", rows[2].RuleId);
        Assert.Contains("앞 2개", rows[2].Status, StringComparison.Ordinal);
        Assert.Contains("나머지 3개", rows[2].Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_stops_when_its_shared_wall_clock_budget_is_exhausted()
    {
        var rows = ProfileRegexDebugBuilder.Build(
            "ABC",
            Enumerable.Range(1, 8)
                .Select(index => Rule(
                    $"slow-{index}",
                    "ABC",
                    ProfileRegexMatchMode.Partial,
                    timeoutMilliseconds: 10_000))
                .ToArray(),
            TimeSpan.FromTicks(1),
            maximumDisplayedRuleCount: 8);

        Assert.InRange(rows.Count, 1, 2);
        Assert.Equal("미리보기 제한", rows[^1].RuleId);
        Assert.Contains("전체 시간 제한", rows[^1].Status, StringComparison.Ordinal);
        Assert.Contains("프로필 검증 대상", rows[^1].Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_rejects_an_overlong_pattern_before_regex_compilation()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            "ABC",
            [Rule(
                "too-long",
                new string('x', ProfileManifestLimits.MaximumRegexPatternLength + 1),
                ProfileRegexMatchMode.Partial)]));

        Assert.Contains("패턴은 최대", row.Status, StringComparison.Ordinal);
        Assert.Empty(HighlightedText(row.Lanes[0]));
    }

    [Fact]
    public void Preview_respects_ignore_case_and_explains_zero_length_matches()
    {
        var rows = ProfileRegexDebugBuilder.Build(
            @"C:\ARCHIVE",
            [
                Rule("ignore-case", "archive", ProfileRegexMatchMode.Partial),
                Rule(
                    "case-sensitive",
                    "archive",
                    ProfileRegexMatchMode.Partial,
                    ignoreCase: false),
                Rule("boundary", "(?<start>^)", ProfileRegexMatchMode.Partial),
            ]);

        Assert.Equal("ARCHIVE", HighlightedText(rows[0].Lanes[0]));
        Assert.Contains("일치하는 구간이 없습니다", rows[1].Status, StringComparison.Ordinal);
        Assert.Contains("길이 0", rows[2].Status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            rows[2].Lanes.SelectMany(static lane => lane.Segments),
            static segment => segment.IsHighlighted);
    }

    [Fact]
    public void Full_check_timeout_keeps_raw_partial_highlights()
    {
        var row = Assert.Single(ProfileRegexDebugBuilder.Build(
            new string('a', 100_000) + "!",
            [Rule(
                "slow-full",
                "a|(a+)+$",
                ProfileRegexMatchMode.Full,
                timeoutMilliseconds: 1)]));

        Assert.Contains("실제 전체 일치 확인 시간 초과", row.Status, StringComparison.Ordinal);
        Assert.NotEmpty(HighlightedText(row.Lanes[0]));
    }

    [Fact]
    public void Rule_accents_are_distinct_and_invalid_pattern_is_reported_without_throwing()
    {
        var rows = ProfileRegexDebugBuilder.Build(
            @"C:\Archive\Example",
            [
                Rule("valid", "Archive", ProfileRegexMatchMode.Partial),
                Rule("invalid", "(?<broken>", ProfileRegexMatchMode.Partial),
            ]);

        Assert.Equal(2, rows.Count);
        Assert.NotEqual(BrushColor(rows[0].AccentBrush), BrushColor(rows[1].AccentBrush));
        Assert.Contains("정규식 오류", rows[1].Status, StringComparison.Ordinal);
        Assert.DoesNotContain(
            rows[1].Lanes[0].Segments,
            static segment => segment.IsHighlighted);
    }

    [Fact]
    public void Directory_exclusion_full_rule_uses_leaf_semantics_and_keeps_named_group_highlights()
    {
        var partial = Assert.Single(ProfileRegexDebugBuilder.BuildDirectoryName(
            "project-name-backup",
            [ExclusionRule("skip-name", "(?<folder>name)", ProfileRegexMatchMode.Full)]));
        var full = Assert.Single(ProfileRegexDebugBuilder.BuildDirectoryName(
            "name",
            [ExclusionRule("skip-name", "(?<folder>name)", ProfileRegexMatchMode.Full)]));

        Assert.Equal("폴더 이름 전체 일치", partial.MatchMode);
        Assert.Contains("부분 후보", partial.Status, StringComparison.Ordinal);
        Assert.Contains("폴더 이름 전체가 일치", partial.Status, StringComparison.Ordinal);
        Assert.Equal(["패턴", "folder"], partial.Lanes.Select(static lane => lane.Label));
        Assert.Equal("name", HighlightedText(partial.Lanes[0]));
        Assert.Equal("name", HighlightedText(partial.Lanes[1]));
        Assert.Contains("실제 폴더 이름 전체 일치", full.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_exclusion_test_result_reports_leaf_match_and_direct_scan_scope()
    {
        var result = ProfilesViewModel.BuildDirectoryExclusionTestResult(
            "name",
            ruleCount: 1,
            new ExcludingProfile());

        Assert.Equal("name", result.LeafName);
        Assert.Contains("skip-name", result.Title, StringComparison.Ordinal);
        Assert.Contains("현재 폴더와 하위", result.Title, StringComparison.Ordinal);
        Assert.Contains("다음 폴더", result.Message, StringComparison.Ordinal);
        Assert.Contains("빠르게 불러오기", result.Message, StringComparison.Ordinal);
        Assert.Contains("검색 루트", result.Message, StringComparison.Ordinal);
        Assert.Contains("기존 인덱스", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_leaf_name_is_taken_from_the_canonical_path()
    {
        var canonicalPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "parent",
            "name"));

        Assert.Equal("name", ProfilesViewModel.GetDirectoryLeafName(canonicalPath));
        Assert.Equal(
            "name",
            ProfilesViewModel.GetDirectoryLeafName(@"\\server\share\parent\name\"));
        Assert.Equal(
            string.Empty,
            ProfilesViewModel.GetDirectoryLeafName(@"\\server\share\"));
    }

    private static ProfileRegexRuleManifest Rule(
        string id,
        string pattern,
        ProfileRegexMatchMode matchMode,
        bool ignoreCase = true,
        int timeoutMilliseconds = 100) =>
        new()
        {
            Id = id,
            Pattern = pattern,
            MatchMode = matchMode,
            IgnoreCase = ignoreCase,
            TimeoutMilliseconds = timeoutMilliseconds,
        };

    private static ProfileDirectoryNameExclusionRuleManifest ExclusionRule(
        string id,
        string pattern,
        ProfileRegexMatchMode matchMode,
        bool ignoreCase = true,
        int timeoutMilliseconds = 100) =>
        new()
        {
            Id = id,
            Pattern = pattern,
            MatchMode = matchMode,
            IgnoreCase = ignoreCase,
            TimeoutMilliseconds = timeoutMilliseconds,
        };

    private static string HighlightedText(ProfileRegexDebugLaneViewModel lane) =>
        string.Concat(
            lane.Segments
                .Where(static segment => segment.IsHighlighted)
                .Select(static segment => segment.Text));

    private static Color BrushColor(Brush brush) =>
        Assert.IsType<SolidColorBrush>(brush).Color;

    private sealed class ExcludingProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(
            "test",
            "1.0.0",
            "Test",
            ProfileCandidateKind.Directory,
            [],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate) =>
            ProfileMapResult.NoMatch();

        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(
            string directoryName) =>
            directoryName == "name"
                ? ProfileDirectoryNameExclusionResult.Excluded("skip-name")
                : ProfileDirectoryNameExclusionResult.NotExcluded();
    }
}
