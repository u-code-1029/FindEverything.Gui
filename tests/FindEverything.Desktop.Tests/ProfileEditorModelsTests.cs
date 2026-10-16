using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileEditorModelsTests
{
    [Fact]
    public void Capture_mode_switches_clear_the_unused_input_and_stale_assignments()
    {
        var field = new ProfileFieldDraftViewModel { FieldId = "code", GroupName = "single" };
        field.SetGuidedAssignment(GuidedSourcePart.Value, new(0, 0, 3, "ABC", "ABC", "ABC", true));
        Assert.True(field.UsesSingleCaptureGroup);
        field.SelectCombinedCaptureGroupsCommand.Execute(null);
        Assert.True(field.UsesCombinedCaptureGroups);
        Assert.Empty(field.GroupName);
        Assert.Empty(field.GuidedAssignments);
        field.GroupNamesText = "prefix, number";
        Assert.Equal(["prefix", "number"], field.EffectiveGroupNames);

        field.SelectSingleCaptureGroupCommand.Execute(null);
        Assert.True(field.UsesSingleCaptureGroup);
        Assert.Empty(field.GroupNamesText);
        Assert.Empty(field.GroupName);
        field.GroupName = "replacement";
        var saved = ProfilesViewModel.BuildFieldManifest(field, 0);
        Assert.Equal("replacement", saved.GroupName);
        Assert.Null(saved.GroupNames);
    }

    [Fact]
    public void Loaded_group_modes_preserve_names_order_and_invalid_conflicts_until_an_explicit_choice()
    {
        var single = new ProfileFieldDraftViewModel(new() { FieldId = "code", GroupName = "alias" });
        Assert.True(single.UsesSingleCaptureGroup);
        Assert.Equal("alias", single.GroupName);
        var combined = new ProfileFieldDraftViewModel(new() { FieldId = "code", GroupNames = ["third", "first", "second"] });
        Assert.True(combined.UsesCombinedCaptureGroups);
        Assert.Equal(["third", "first", "second"], combined.EffectiveGroupNames);
        Assert.Equal(["third", "first", "second"], ProfilesViewModel.BuildFieldManifest(combined, 0).GroupNames);
        var conflict = new ProfileFieldDraftViewModel(new()
        { FieldId = "code", GroupName = "legacy", GroupNames = ["prefix", "number"] });
        Assert.True(conflict.HasConflictingGroupInputs);
        Assert.Equal("legacy", conflict.GroupName);
        conflict.SelectCombinedCaptureGroupsCommand.Execute(null);
        Assert.False(conflict.HasConflictingGroupInputs);
        Assert.Empty(conflict.GroupName);
        Assert.Equal(["prefix", "number"], conflict.EffectiveGroupNames);
    }

    [Fact]
    public void Guided_date_preset_transitions_keep_all_new_parts_and_only_reset_the_old_arrangement()
    {
        var field = new ProfileFieldDraftViewModel { FieldId = "takenOn", Kind = ProfileFieldValueKind.DateTime };
        var year = new GuidedPathChoiceViewModel(0, 0, 4, "2026", "2026", "2026", true);
        var monthDay = new GuidedPathChoiceViewModel(1, 5, 4, "0521", "0521", "0521", true);
        field.EnsureDateDefaults();
        field.SetGuidedAssignment(GuidedSourcePart.Value, year);
        Assert.True(field.UsesSingleCaptureGroup);
        field.DateSourcePreset = GuidedDateSourcePreset.YearAndMonthDay;
        Assert.True(field.UsesCombinedCaptureGroups);
        Assert.Empty(field.GuidedAssignments);
        field.SetGuidedAssignment(GuidedSourcePart.Year, year);
        field.SetGuidedAssignment(GuidedSourcePart.MonthDay, monthDay);
        Assert.Equal(2, field.GuidedAssignments.Count);
        Assert.Equal(["takenOnYear", "takenOnMonthDay"], field.EffectiveGroupNames);
        field.DateSourcePreset = GuidedDateSourcePreset.SingleValue;
        Assert.True(field.UsesSingleCaptureGroup);
        Assert.Empty(field.GroupNamesText);
        Assert.Empty(field.GuidedAssignments);
        field.SetGuidedAssignment(GuidedSourcePart.Value, year);
        Assert.Single(field.GuidedAssignments);
        field.DateSourcePreset = GuidedDateSourcePreset.YearAndMonthDay;
        field.SetGuidedAssignment(GuidedSourcePart.Year, year);
        field.SetGuidedAssignment(GuidedSourcePart.MonthDay, monthDay);
        Assert.Equal(2, field.GuidedAssignments.Count);
        Assert.True(field.UsesCombinedCaptureGroups);
    }

    [Fact]
    public void A_loaded_one_element_group_list_keeps_its_capture_name_when_used_in_guided_mode()
    {
        var field = new ProfileFieldDraftViewModel(new() { FieldId = "field", GroupNames = ["originalGroup"] });
        Assert.True(field.UsesCombinedCaptureGroups);
        field.SetGuidedAssignment(GuidedSourcePart.Value, new(0, 0, 1, "A", "A", "A", true));
        Assert.Equal(["originalGroup"], field.EffectiveGroupNames);
        Assert.True(field.UsesSingleCaptureGroup);
        Assert.Single(field.GuidedAssignments);
    }
    [Fact]
    public void Regex_timeout_draft_preserves_invalid_text_until_it_is_corrected()
    {
        var row = new ProfileRuleDraftViewModel { TimeoutMilliseconds = 250 };
        Assert.Equal("250", row.TimeoutMillisecondsText);

        row.TimeoutMillisecondsText = "not-a-number";
        Assert.True(row.HasNumericValidationError);
        Assert.Equal("not-a-number", row.TimeoutMillisecondsText);

        row.TimeoutMillisecondsText = "500";
        Assert.False(row.HasNumericValidationError);
        Assert.Equal(500, row.TimeoutMilliseconds);
    }

    [Fact]
    public void Text_file_size_and_order_drafts_reject_overflow_and_out_of_range_values()
    {
        var row = new ProfileTextFileFieldDraftViewModel
        {
            MaxBytesText = "0",
            OrderText = "2147483648",
        };
        Assert.True(row.HasNumericValidationError);

        row.MaxBytesText = "4096";
        row.OrderText = "-10";
        Assert.False(row.HasNumericValidationError);
        Assert.Equal(4096L, row.MaxBytes);
        Assert.Equal(-10, row.Order);
    }

    [Fact]
    public void Directory_path_exclusion_draft_preserves_regex_options()
    {
        var draft = new ProfileDirectoryExclusionRuleDraftViewModel(
            new ProfileDirectoryExclusionRuleManifest
            {
                Id = "skip-cache",
                Pattern = "temp|cache",
                MatchMode = ProfileRegexMatchMode.Partial,
                IgnoreCase = true,
                TimeoutMilliseconds = 250,
            });

        Assert.Equal("skip-cache", draft.Id);
        Assert.Equal("temp|cache", draft.Pattern);
        Assert.Equal(ProfileRegexMatchMode.Partial, draft.MatchMode);
        Assert.True(draft.IgnoreCase);
        Assert.Equal(250, draft.TimeoutMilliseconds);
        Assert.Equal("절대경로 부분 일치", draft.MatchModeDisplayName);
        draft.MatchMode = ProfileRegexMatchMode.Full;
        Assert.Equal("절대경로 전체 일치", draft.MatchModeDisplayName);
    }

    [Fact]
    public void Text_file_field_draft_preserves_file_matching_and_read_limits()
    {
        var draft = new ProfileTextFileFieldDraftViewModel(
            new ProfileTextFileFieldManifest
            {
                FieldId = "description",
                Header = "설명",
                Order = 40,
                Required = true,
                FileNamePattern = @"^info-\d+\.txt$",
                MatchMode = ProfileRegexMatchMode.Partial,
                IgnoreCase = false,
                TimeoutMilliseconds = 250,
                MaxBytes = 4096,
            });

        Assert.Equal("description", draft.FieldId);
        Assert.Equal("설명", draft.Header);
        Assert.Equal(40, draft.Order);
        Assert.True(draft.Required);
        Assert.Equal(@"^info-\d+\.txt$", draft.FileNamePattern);
        Assert.Equal(ProfileRegexMatchMode.Partial, draft.MatchMode);
        Assert.False(draft.IgnoreCase);
        Assert.Equal(250, draft.TimeoutMilliseconds);
        Assert.Equal(4096, draft.MaxBytes);
        Assert.Equal("파일 이름 일부 일치", draft.MatchModeDisplayName);
    }

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
    public void Alias_value_mapping_round_trip_preserves_rows_and_notifies_edits()
    {
        var draft = new ProfileFieldDraftViewModel(new ProfileFieldManifest
        {
            FieldId = "employee",
            GroupName = "alias",
            Header = "담당자",
            Kind = ProfileFieldValueKind.String,
            ValueMappings =
            [
                new ProfileValueMappingManifest { Source = "A", Display = "홍길동" },
            ],
        });
        var valueMappingsChanged = 0;
        draft.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ProfileFieldDraftViewModel.ValueMappings))
            {
                valueMappingsChanged++;
            }
        };

        Assert.True(draft.HasAliasGroup);
        Assert.True(draft.HasValueMappings);
        Assert.True(draft.CanConfigureValueMappings);
        var mapping = Assert.Single(draft.ValueMappings);
        Assert.Equal("A", mapping.Source);
        Assert.Equal("홍길동", mapping.Display);

        mapping.Display = "Hong Gil-dong";

        Assert.Equal(1, valueMappingsChanged);
        draft.Kind = ProfileFieldValueKind.Int32;
        Assert.False(draft.CanConfigureValueMappings);
        Assert.True(draft.ShowValueMappingEditor);

        draft.RemoveValueMappingCommand.Execute(mapping);
        Assert.Empty(draft.ValueMappings);
        Assert.False(draft.HasValueMappings);
        Assert.False(draft.ShowValueMappingEditor);
    }

    [Fact]
    public void Profile_field_manifest_builder_preserves_value_mapping_whitespace()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            FieldId = "employee",
            GroupName = "alias",
            Header = "담당자",
            Kind = ProfileFieldValueKind.String,
        };
        draft.ValueMappings.Add(new ProfileValueMappingDraftViewModel
        {
            Source = " A ",
            Display = " 홍길동 ",
        });

        var manifest = ProfilesViewModel.BuildFieldManifest(draft, index: 0);

        var mapping = Assert.Single(manifest.ValueMappings!);
        Assert.Equal(" A ", mapping.Source);
        Assert.Equal(" 홍길동 ", mapping.Display);
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
    public void Guided_date_format_choices_use_examples_and_update_the_manifest_value()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            Kind = ProfileFieldValueKind.DateTime,
        };
        draft.EnsureDateDefaults();

        Assert.Contains(
            draft.DateFormatChoices,
            static choice => choice.Format == "yyyy-MM-dd"
                && choice.DisplayName.Contains("2026-05-21", StringComparison.Ordinal));
        Assert.Contains(
            draft.DateFormatChoices,
            static choice => choice.Format == "dd-MM-yyyy"
                && choice.DisplayName.Contains("21-05-2026", StringComparison.Ordinal));

        draft.ParseFormat = "yyyy.MM.dd";

        Assert.Equal("yyyy.MM.dd", draft.ParseFormat);
    }

    [Fact]
    public void Guided_date_format_choices_preserve_an_expert_custom_format()
    {
        var draft = new ProfileFieldDraftViewModel(new ProfileFieldManifest
        {
            FieldId = "taken-on",
            GroupName = "takenOn",
            Header = "촬영일",
            Required = true,
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "yyyyMMdd-HHmm",
        });

        var custom = Assert.Single(
            draft.DateFormatChoices,
            static choice => choice.Format == "yyyyMMdd-HHmm");
        Assert.Contains("사용자 지정", custom.DisplayName, StringComparison.Ordinal);
        Assert.Equal("yyyyMMdd-HHmm", draft.ParseFormat);
    }

    [Fact]
    public void Guided_date_assignment_suggests_the_format_from_the_selected_example()
    {
        var single = new ProfileFieldDraftViewModel
        {
            FieldId = "taken-on",
            Kind = ProfileFieldValueKind.DateTime,
        };
        single.EnsureDateDefaults();
        single.SetGuidedAssignment(
            GuidedSourcePart.Value,
            new GuidedPathChoiceViewModel(
                0, 0, 10, "2026-05-21", "2026-05-21", "날짜 폴더 전체", true));

        var split = new ProfileFieldDraftViewModel
        {
            FieldId = "taken-on",
            Kind = ProfileFieldValueKind.DateTime,
            DateSourcePreset = GuidedDateSourcePreset.YearAndMonthDay,
        };
        split.SetGuidedAssignment(
            GuidedSourcePart.Year,
            new GuidedPathChoiceViewModel(0, 0, 4, "2026", "2026", "연도 폴더", true));
        split.SetGuidedAssignment(
            GuidedSourcePart.MonthDay,
            new GuidedPathChoiceViewModel(1, 5, 4, "0521", "0521", "월일 조각", false));

        Assert.Equal("yyyy-MM-dd", single.ParseFormat);
        Assert.Equal("yyyyMMdd", split.ParseFormat);
        Assert.Collection(
            split.DateFormatChoices,
            choice => Assert.Equal("yyyyMMdd", choice.Format));
    }

    [Fact]
    public void Guided_date_assignment_preserves_an_ambiguous_format_selected_by_the_user()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            FieldId = "taken-on",
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "dd-MM-yyyy",
        };

        draft.SetGuidedAssignment(
            GuidedSourcePart.Value,
            new GuidedPathChoiceViewModel(
                0, 0, 10, "05-06-2026", "05-06-2026", "날짜 폴더 전체", true));

        Assert.Equal("dd-MM-yyyy", draft.ParseFormat);
    }

    [Fact]
    public void Changing_a_date_field_to_text_removes_hidden_date_formats()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "yyyy-MM-dd",
            DisplayFormat = "yyyy년 MM월 dd일",
        };

        draft.Kind = ProfileFieldValueKind.String;

        Assert.Equal(string.Empty, draft.ParseFormat);
        Assert.Equal(string.Empty, draft.DisplayFormat);
        Assert.Equal(GuidedDateSourcePreset.SingleValue, draft.DateSourcePreset);
    }

    [Fact]
    public void Changing_kind_does_not_rewrite_expert_composite_groups()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            FieldId = "code",
            GroupNamesText = "prefix, number",
            Kind = ProfileFieldValueKind.String,
        };

        draft.Kind = ProfileFieldValueKind.DateTime;
        Assert.Equal(["prefix", "number"], draft.EffectiveGroupNames);

        draft.Kind = ProfileFieldValueKind.String;
        Assert.Equal(["prefix", "number"], draft.EffectiveGroupNames);
    }

    [Fact]
    public void Changing_between_numeric_kinds_preserves_a_valid_display_format()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            Kind = ProfileFieldValueKind.Int32,
            DisplayFormat = "N2",
        };

        draft.Kind = ProfileFieldValueKind.Decimal;

        Assert.Equal("N2", draft.DisplayFormat);
    }

    [Fact]
    public void Invalid_custom_date_format_does_not_escape_guided_auto_detection()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            FieldId = "taken-on",
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "%",
        };

        var exception = Record.Exception(() => draft.SetGuidedAssignment(
            GuidedSourcePart.Value,
            new GuidedPathChoiceViewModel(
                0, 0, 10, "2026-05-21", "2026-05-21", "날짜 폴더 전체", true)));

        Assert.Null(exception);
        Assert.Equal("yyyy-MM-dd", draft.ParseFormat);
    }

    [Fact]
    public void Date_format_value_notifies_before_its_choice_list_is_rebuilt()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            Kind = ProfileFieldValueKind.DateTime,
            ParseFormat = "%",
        };
        List<string?> notifications = [];
        draft.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        draft.ParseFormat = "yyyy-MM-dd";

        Assert.True(
            notifications.IndexOf(nameof(ProfileFieldDraftViewModel.ParseFormat))
            < notifications.IndexOf(nameof(ProfileFieldDraftViewModel.DateFormatChoices)));
    }

    [Fact]
    public void Guided_date_validation_rejects_a_format_that_cannot_parse_the_selected_parts()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            FieldId = "taken-on",
            Header = "촬영일",
            Kind = ProfileFieldValueKind.DateTime,
            DateSourcePreset = GuidedDateSourcePreset.YearMonthAndDay,
        };
        draft.SetGuidedAssignment(
            GuidedSourcePart.Year,
            new GuidedPathChoiceViewModel(0, 0, 4, "2026", "2026", "연도", true));
        draft.SetGuidedAssignment(
            GuidedSourcePart.Month,
            new GuidedPathChoiceViewModel(1, 5, 1, "5", "5", "월", true));
        draft.SetGuidedAssignment(
            GuidedSourcePart.Day,
            new GuidedPathChoiceViewModel(2, 7, 2, "21", "21", "일", true));

        var isValid = draft.TryValidateGuidedDateSample(out var errorMessage);

        Assert.False(isValid);
        Assert.Equal("2026521", draft.GuidedSampleInput);
        Assert.Contains("2026521", errorMessage, StringComparison.Ordinal);
        Assert.Contains("날짜 모양", errorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Time_input_choice_keeps_time_in_the_default_display_format()
    {
        var draft = new ProfileFieldDraftViewModel
        {
            Kind = ProfileFieldValueKind.DateTime,
        };

        draft.ParseFormat = "yyyyMMdd_HHmmss";

        Assert.Equal("yyyy-MM-dd HH:mm:ss", draft.DisplayFormat);
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
