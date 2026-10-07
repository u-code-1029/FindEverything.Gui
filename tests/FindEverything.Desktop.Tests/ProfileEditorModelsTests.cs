using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileEditorModelsTests
{
    [Fact]
    public void Composite_date_manifest_round_trip_preserves_group_order_and_custom_formats()
    {
        var source = new ProfileFieldManifest
        {
            FieldId = "taken-on",
            GroupNames = ["year", "monthDay"],
            Header = "촬영일",
            Required = true,
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "yyyyMMdd",
            DisplayFormat = "yyyy년 MM월 dd일",
        };

        var draft = new ProfileFieldDraftViewModel(source);

        Assert.Equal(GuidedDateSourcePreset.YearAndMonthDay, draft.DateSourcePreset);
        Assert.Equal(["year", "monthDay"], draft.EffectiveGroupNames);
        Assert.Equal("yyyyMMdd", draft.ParseFormat);
        Assert.Equal("yyyy년 MM월 dd일", draft.DisplayFormat);
        Assert.True(draft.SupportsGuidedAssignments);
    }

    [Fact]
    public void Guided_mode_rejects_ambiguous_or_non_date_composite_groups()
    {
        var reversedDate = new ProfileFieldDraftViewModel(new ProfileFieldManifest
        {
            FieldId = "taken-on",
            GroupNames = ["month", "year"],
            Header = "촬영일",
            Required = true,
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "MMyyyy",
        });
        var compositeText = new ProfileFieldDraftViewModel(new ProfileFieldManifest
        {
            FieldId = "code",
            GroupNames = ["prefix", "number"],
            Header = "코드",
            Required = true,
            Kind = ProfileFieldValueKind.String,
        });

        Assert.False(reversedDate.SupportsGuidedAssignments);
        Assert.False(compositeText.SupportsGuidedAssignments);
    }

    [Fact]
    public void Invalid_manifest_with_both_group_sources_is_not_silently_rewritten()
    {
        var draft = new ProfileFieldDraftViewModel(new ProfileFieldManifest
        {
            FieldId = "taken-on",
            GroupName = "legacyDate",
            GroupNames = ["year", "monthDay"],
            Header = "촬영일",
            Required = true,
            Kind = ProfileFieldValueKind.DateTime,
        });

        Assert.Equal("legacyDate", draft.GroupName);
        Assert.Equal("year, monthDay", draft.GroupNamesText);
    }

    [Fact]
    public void Date_preset_assignments_keep_order_and_describe_button_choices()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            FieldId = "taken-on",
            Header = "촬영일",
            Kind = ProfileFieldValueKind.DateTime,
            DateSourcePreset = GuidedDateSourcePreset.YearAndMonthDay,
            Required = true,
        };
        var year = new GuidedPathChoiceViewModel(
            0, 0, 4, "2026", "2026", "‘2026’ 폴더 전체", true);
        var monthDay = new GuidedPathChoiceViewModel(
            1, 5, 4, "0521", "0521", "‘0521_Project’의 ‘0521’ 부분", false);

        draft.SetGuidedAssignment(GuidedSourcePart.Year, year);
        draft.SetGuidedAssignment(GuidedSourcePart.MonthDay, monthDay);

        Assert.Equal(["taken_onYear", "taken_onMonthDay"], draft.EffectiveGroupNames);
        Assert.Equal("yyyyMMdd", draft.ParseFormat);
        Assert.Equal("yyyy-MM-dd", draft.DisplayFormat);
        Assert.Contains("연도: ‘2026’ 폴더 전체", draft.SourceSummary);
        Assert.Contains("월일: ‘0521_Project’의 ‘0521’ 부분", draft.SourceSummary);
    }

    [Fact]
    public void Expert_rule_round_trip_preserves_terminal_group_order()
    {
        var draft = new ProfileRuleDraftViewModel(new ProfileRegexRuleManifest
        {
            Id = "leaf",
            Pattern = "test",
            StopTraversalWhenCapturedGroups = ["year", "monthDay"],
        });

        Assert.Equal("year, monthDay", draft.StopTraversalGroupsText);
    }

    [Fact]
    public void Loaded_profile_inspection_exposes_composite_and_terminal_group_order()
    {
        var field = new ProfileFieldInspectionViewModel(new ProfileFieldDescriptor(
            "taken-on",
            "year",
            "촬영일",
            10,
            true,
            ProfileFieldValueKind.DateTime,
            false,
            "yyyyMMdd",
            "yyyy-MM-dd")
        {
            GroupNames = ["year", "monthDay"],
        });
        var rule = new ProfileRuleInspectionViewModel(new ProfileRegexRuleDescriptor(
            1,
            "leaf",
            "test",
            ProfileRegexMatchMode.Full,
            true,
            100)
        {
            StopTraversalWhenCapturedGroups = ["year", "monthDay"],
        });

        Assert.Equal("year → monthDay", field.CaptureGroups);
        Assert.Equal("year + monthDay", rule.StopTraversalGroups);
    }
}
