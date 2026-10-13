using System.IO;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfilesValidationTests
{
    [Theory]
    [InlineData("profile_id_invalid", ProfileEditorStep.Profile)]
    [InlineData("profile_display_name_missing", ProfileEditorStep.Profile)]
    [InlineData("contract_unsupported", ProfileEditorStep.Profile)]
    [InlineData("capture_field_id_invalid", ProfileEditorStep.PathAndFields)]
    [InlineData("capture_parse_format_invalid", ProfileEditorStep.PathAndFields)]
    [InlineData("regex_invalid", ProfileEditorStep.PathAndFields)]
    [InlineData("regex_timeout_budget_exceeded", ProfileEditorStep.Review)]
    [InlineData("selection_regex_requires_v4", ProfileEditorStep.SelectionRules)]
    [InlineData("required_group_not_defined", ProfileEditorStep.PathAndFields)]
    [InlineData("stop_traversal_group_not_defined", ProfileEditorStep.PathAndFields)]
    [InlineData("text_file_creation_template_invalid", ProfileEditorStep.DataSources)]
    [InlineData("child_directory_name_invalid", ProfileEditorStep.DataSources)]
    [InlineData("user_input_field_id_duplicate", ProfileEditorStep.DataSources)]
    [InlineData("capture_value_mapping_source_duplicate", ProfileEditorStep.DataSources)]
    [InlineData("selection_match_rule_invalid", ProfileEditorStep.SelectionRules)]
    [InlineData("excluded_directory_name_regex_invalid", ProfileEditorStep.ScanOptions)]
    [InlineData("scan_depth_invalid", ProfileEditorStep.ScanOptions)]
    [InlineData("custom_plugin_failure", ProfileEditorStep.Review)]
    public void Stable_codes_route_to_the_actual_editor_section(string code, ProfileEditorStep expected)
        => Assert.Equal(expected, ProfilesViewModel.GetValidationStep(code));

    [Fact]
    public void Validation_errors_are_not_truncated_and_navigation_opens_the_target_step()
    {
        var service = new StubAuthoringService
        {
            Review = new(null, Enumerable.Range(0, 12)
                .Select(index => new ProfileDiagnostic(ProfileDiagnosticSeverity.Error,
                    "text_file_name_pattern_missing", $"Missing filename pattern {index}")).ToArray()),
        };
        var vm = Create(service);

        vm.ValidateDraftCommand.Execute(null);

        Assert.True(vm.HasValidationIssues);
        Assert.Equal(12, vm.ValidationIssues.Count);
        Assert.All(vm.ValidationIssues, issue => Assert.Equal(ProfileEditorStep.DataSources, issue.Step));
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, vm.EditorStatusSeverity);
        vm.GoToEditorStepCommand.Execute(vm.ValidationIssues[0].Step);
        Assert.Equal(ProfileEditorStep.DataSources, vm.CurrentEditorStep);

        vm.DraftDisplayName = "Edited profile";
        Assert.False(vm.HasValidationIssues);
    }

    [Fact]
    public async Task Save_validation_failure_is_actionable_without_saving_the_profile()
    {
        var service = new StubAuthoringService
        {
            Review = new(null, [new(ProfileDiagnosticSeverity.Error, "selection_match_rule_invalid", "Check regex groups")]),
        };
        var vm = Create(service);

        await vm.SaveDraftCommand.ExecuteAsync(null);

        var issue = Assert.Single(vm.ValidationIssues);
        Assert.Equal(ProfileEditorStep.SelectionRules, issue.Step);
        Assert.True(vm.IsDraftDirty);
        Assert.Equal(1, service.SaveAttempts);
    }

    [Fact]
    public void Invalid_numeric_input_remains_visible_while_save_is_disabled_and_clears_when_fixed()
    {
        var vm = Create(new StubAuthoringService());
        vm.DraftRules[0].TimeoutMillisecondsText = "not a number";
        vm.DraftMaxScanDepthText = "-1";

        Assert.False(vm.SaveDraftCommand.CanExecute(null));
        Assert.False(vm.ValidateDraftCommand.CanExecute(null));
        Assert.Contains(vm.ValidationIssues, issue => issue.Step == ProfileEditorStep.PathAndFields);
        Assert.Contains(vm.ValidationIssues, issue => issue.Step == ProfileEditorStep.ScanOptions);
        vm.DraftRules[0].TimeoutMillisecondsText = "100";
        Assert.DoesNotContain(vm.ValidationIssues, issue => issue.Step == ProfileEditorStep.PathAndFields);
        Assert.Contains(vm.ValidationIssues, issue => issue.Step == ProfileEditorStep.ScanOptions);
        vm.DraftMaxScanDepthText = "256";
        Assert.False(vm.HasValidationIssues);
        Assert.True(vm.SaveDraftCommand.CanExecute(null));
    }

    [Fact]
    public void Guided_template_build_failure_links_to_path_and_fields_instead_of_an_unactionable_summary()
    {
        var vm = Create(new StubAuthoringService());
        vm.DraftPathTemplate = "{missing@missing}";

        vm.ValidateDraftCommand.Execute(null);

        Assert.True(vm.HasValidationIssues);
        Assert.All(vm.ValidationIssues, issue => Assert.Equal(ProfileEditorStep.PathAndFields, issue.Step));
        Assert.Contains(vm.ValidationIssues, issue => issue.Code != "draft_build_failed");
        vm.DraftPathTemplate = "{name@name}";
        Assert.False(vm.HasValidationIssues);
    }

    [Fact]
    public async Task Typing_numeric_errors_does_not_steal_scroll_but_an_explicit_save_failure_reveals_the_list()
    {
        var vm = Create(new StubAuthoringService());
        var revealRequests = 0;
        vm.ValidationIssuesRequested += (_, _) => revealRequests++;
        vm.DraftMaxScanDepthText = "invalid";
        Assert.True(vm.HasValidationIssues);
        Assert.Equal(0, revealRequests);

        Assert.False(await vm.SaveChangesAsync());
        Assert.Equal(1, revealRequests);
        Assert.Contains(vm.ValidationIssues, issue => issue.Step == ProfileEditorStep.ScanOptions);
    }

    [Fact]
    public void Successful_revalidation_removes_old_definition_errors()
    {
        var service = new StubAuthoringService
        {
            Review = new(null, [new(ProfileDiagnosticSeverity.Error, "profile_id_invalid", "Invalid ID")]),
        };
        var vm = Create(service);
        vm.ValidateDraftCommand.Execute(null);
        Assert.True(vm.HasValidationIssues);

        service.Review = new(new StubLoadedProfile(), []);
        vm.ValidateDraftCommand.Execute(null);
        Assert.False(vm.HasValidationIssues);
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Success, vm.EditorStatusSeverity);
    }

    private static ProfilesViewModel Create(StubAuthoringService service) => new(
        new StubCatalog(), service, new ProfilePathTemplateCompiler(), new FixedCanonicalizer(),
        null!, new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
        null!, null!, null!, new TestAppLocalizer(), NullLogger<ProfilesViewModel>.Instance);

    internal static ProfilesViewModel CreateForUi() => Create(new StubAuthoringService());

    private sealed class FixedCanonicalizer : IProfilePathCanonicalizer
    {
        public string Canonicalize(string path) => @"C:\Example";
    }

    private sealed class StubCatalog : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current => ProfileCatalogSnapshot.Empty;
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }

    private sealed class StubLoadedProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("sample", "1", "Sample", ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }

    private sealed class StubAuthoringService : IProfileAuthoringService
    {
        public ProfileDefinitionReview Review { get; set; } = new(new StubLoadedProfile(), []);
        public int SaveAttempts { get; private set; }
        public string UserProfilesDirectory => Path.GetTempPath();
        public Task<IReadOnlyList<EditableProfileSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EditableProfileSummary>>([]);
        public Task<ProfileManifest?> LoadAsync(EditableProfileSummary profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProfileManifest?>(null);
        public ProfileDefinitionReview Validate(ProfileManifest manifest) => Review;
        public ProfileDefinitionTestResult Test(ProfileManifest manifest, string path) => throw new NotSupportedException();
        public Task<ProfileSaveResult> SaveAndApplyAsync(ProfileManifest manifest, string? originalProfileId,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return Task.FromResult(new ProfileSaveResult(false, false, Review, null));
        }
    }
}
