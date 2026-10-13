using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileAdditionalEditorModelsTests
{
    [Fact]
    public void Six_step_order_keeps_data_sources_selection_and_scan_options_separate()
    {
        Assert.Equal([ProfileEditorStep.Profile, ProfileEditorStep.PathAndFields, ProfileEditorStep.DataSources,
            ProfileEditorStep.SelectionRules, ProfileEditorStep.ScanOptions, ProfileEditorStep.Review], Enum.GetValues<ProfileEditorStep>());
    }
    [Fact]
    public void Folder_presence_draft_round_trips_literal_name_and_case()
    {
        var source = new ProfileChildDirectoryFieldManifest { FieldId = "ready", Header = "Ready", DirectoryName = "READY", IgnoreCase = false, Order = 30 };
        var draft = new ProfileChildDirectoryFieldDraftViewModel(source);
        var saved = draft.ToManifest(); Assert.Equal("READY", saved.DirectoryName); Assert.False(saved.IgnoreCase); Assert.Equal(30, saved.Order);
        draft.OrderText = "not-number"; Assert.NotEmpty(draft.NumericValidationMessage);
    }
    [Fact]
    public void New_text_fields_enable_explicit_editing_but_legacy_definitions_preserve_disabled_default()
    {
        Assert.True(new ProfileTextFileFieldDraftViewModel().EditEnabled);
        Assert.False(new ProfileTextFileFieldDraftViewModel(new()).EditEnabled);
        var draft = new ProfileTextFileFieldDraftViewModel(new() { EditOptions = new() { Enabled = true, CreationFileNameTemplate = "{name}.txt" } });
        Assert.True(draft.EditEnabled); Assert.Equal("{name}.txt", draft.CreationFileNameTemplate);
    }
    [Fact]
    public void Selection_draft_round_trips_rule_and_column_comparison_settings()
    {
        var draft = new ProfileSelectionRuleDraftViewModel(new()
        {
            Id = "excel", DisplayName = "Excel", InputKind = ProfileSelectionInputKind.Table, HasHeaderRow = true,
            Delimiter = ";", IgnoreCase = false, TimeoutMilliseconds = 500,
            Columns = [new() { FieldId = "date", InputHeader = "Date", InputColumnIndex = 2,
                ValueSource = ProfileSelectionValueSource.Original, DateComparison = ProfileSelectionDateComparison.Exact, DateFormat = "yyyyMMdd" }],
        });
        var rule = draft.ToManifest(); Assert.Equal(";", rule.Delimiter); Assert.Equal(500, rule.TimeoutMilliseconds);
        Assert.Equal(ProfileSelectionValueSource.Original, rule.Columns[0].ValueSource);
        Assert.Equal(ProfileSelectionDateComparison.Exact, rule.Columns[0].DateComparison);
        Assert.Equal("yyyyMMdd", rule.Columns[0].DateFormat);
        draft.Columns[0].InputColumnIndexText = "-1"; Assert.NotEmpty(draft.NumericValidationMessage);
    }
    [Fact]
    public void Field_chips_add_only_one_mapping_per_field()
    {
        var draft = new ProfileSelectionRuleDraftViewModel(); draft.AddFieldCommand.Execute("name"); draft.AddFieldCommand.Execute("name");
        Assert.Equal("name", Assert.Single(draft.Columns).FieldId); Assert.Equal("1", draft.Columns[0].InputColumnIndexText);
    }
}
