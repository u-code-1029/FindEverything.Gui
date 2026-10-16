using CommunityToolkit.Mvvm.ComponentModel;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

/// <summary>A persistent, actionable editor error, separate from transient operation notifications.</summary>
public sealed record ProfileValidationIssueViewModel(
    string Code,
    string Message,
    ProfileEditorStep Step,
    string NavigationText);

public partial class ProfilesViewModel
{
    private IReadOnlyList<ProfileValidationIssueViewModel> _definitionValidationIssues = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationIssues))]
    private IReadOnlyList<ProfileValidationIssueViewModel> _validationIssues = [];

    public bool HasValidationIssues => ValidationIssues.Count != 0;

    public event EventHandler? ValidationIssuesRequested;

    private void ClearDefinitionValidationIssues()
    {
        if (_definitionValidationIssues.Count == 0) return;
        _definitionValidationIssues = [];
        RefreshValidationIssues();
    }

    private void ApplyDefinitionValidationIssues(IEnumerable<ProfileDiagnostic> diagnostics)
    {
        _definitionValidationIssues = diagnostics
            .Where(static diagnostic => diagnostic.Severity == ProfileDiagnosticSeverity.Error)
            .Select(diagnostic => CreateValidationIssue(diagnostic.Code,
                ProfileDiagnosticLocalizer.Translate(_localizer, diagnostic),
                GetValidationStep(diagnostic.Code)))
            .ToArray();
        RefreshValidationIssues();
        if (HasValidationIssues) ValidationIssuesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyBuildValidationIssues(InvalidOperationException exception)
    {
        if (IsGuidedMode)
        {
            var dates = DraftFields.Where(field => !field.TryValidateGuidedDateSample(out _))
                .Select(field =>
                {
                    field.TryValidateGuidedDateSample(out var message);
                    return CreateValidationIssue("guided_date_invalid", message, ProfileEditorStep.PathAndFields);
                }).ToArray();
            if (dates.Length > 0)
            {
                _definitionValidationIssues = dates;
                RefreshValidationIssues();
                ValidationIssuesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            var result = _pathTemplateCompiler.Compile(DraftPathTemplate, BuildFieldManifests());
            if (!result.IsValid && result.Diagnostics.Count > 0)
            {
                _definitionValidationIssues = result.Diagnostics.Select(diagnostic => CreateValidationIssue(
                    diagnostic.Code, ProfileDiagnosticLocalizer.Translate(_localizer, diagnostic),
                    ProfileEditorStep.PathAndFields)).ToArray();
                RefreshValidationIssues();
                ValidationIssuesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        _definitionValidationIssues = [CreateValidationIssue("draft_build_failed", exception.Message,
            IsGuidedMode ? ProfileEditorStep.PathAndFields : ProfileEditorStep.Review)];
        RefreshValidationIssues();
        ValidationIssuesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshValidationIssues()
    {
        // These errors remain visible even while validation/save commands are disabled for invalid numeric input.
        var numeric = new List<ProfileValidationIssueViewModel>();
        foreach (var row in DraftRules) AddNumeric(row.NumericValidationMessage, "regex_numeric_invalid", ProfileEditorStep.PathAndFields, row.Id);
        foreach (var row in DraftExcludedDirectoryNameRules) AddNumeric(row.NumericValidationMessage, "excluded_directory_numeric_invalid", ProfileEditorStep.ScanOptions, row.Id);
        foreach (var row in DraftTextFileFields) AddNumeric(row.NumericValidationMessage, "text_file_numeric_invalid", ProfileEditorStep.DataSources, row.FieldId);
        foreach (var row in DraftUserInputFields) AddNumeric(row.NumericValidationMessage, "user_input_numeric_invalid", ProfileEditorStep.DataSources, row.FieldId);
        foreach (var row in DraftChildDirectoryFields) AddNumeric(row.NumericValidationMessage, "child_directory_numeric_invalid", ProfileEditorStep.DataSources, row.FieldId);
        foreach (var row in DraftSelectionRules) AddNumeric(row.NumericValidationMessage, "selection_numeric_invalid", ProfileEditorStep.SelectionRules, row.Id);
        AddNumeric(ScanDepthValidationMessage, "scan_depth_invalid", ProfileEditorStep.ScanOptions);
        var issues = numeric.Concat(_definitionValidationIssues)
            .DistinctBy(static issue => (issue.Code, issue.Message, issue.Step)).ToArray();
        if (!ValidationIssues.SequenceEqual(issues)) ValidationIssues = issues;

        void AddNumeric(string message, string code, ProfileEditorStep step, string? reference = null)
        {
            if (!string.IsNullOrEmpty(message)) numeric.Add(CreateValidationIssue(code,
                string.IsNullOrWhiteSpace(reference) ? message : $"[{reference}] {message}", step));
        }
    }

    private ProfileValidationIssueViewModel CreateValidationIssue(string code, string message, ProfileEditorStep step)
    {
        var label = GetEditorStepTitle(step);
        return new(code, message, step, F("Loc.Profiles.Validation.GoToStep", "{0} 이동", label));
    }

    internal static ProfileEditorStep GetValidationStep(string code)
    {
        if (code == "workspace_entry_paths_invalid") return ProfileEditorStep.Profile;
        if (code == "regex_timeout_budget_exceeded") return ProfileEditorStep.Review;
        // Stable diagnostic codes, not localized message text, determine the target step.
        if (code.StartsWith("selection_", StringComparison.Ordinal)) return ProfileEditorStep.SelectionRules;
        if (code.StartsWith("excluded_directory_", StringComparison.Ordinal) || code == "scan_depth_invalid") return ProfileEditorStep.ScanOptions;
        if (code.StartsWith("text_file_", StringComparison.Ordinal)
            || code.StartsWith("user_input_", StringComparison.Ordinal)
            || code.StartsWith("child_directory_", StringComparison.Ordinal)
            || code.StartsWith("capture_value_mapping_", StringComparison.Ordinal)) return ProfileEditorStep.DataSources;
        if (code.StartsWith("capture_", StringComparison.Ordinal)
            || code.StartsWith("regex_", StringComparison.Ordinal)
            || code.StartsWith("template_", StringComparison.Ordinal)
            || code.StartsWith("stop_traversal_", StringComparison.Ordinal)
            || code is "required_group_not_defined" or "optional_field_not_nullable" or "path_input_removed") return ProfileEditorStep.PathAndFields;
        if (code.StartsWith("profile_", StringComparison.Ordinal)
            || code is "contract_unsupported" or "candidate_kind_invalid") return ProfileEditorStep.Profile;
        return ProfileEditorStep.Review;
    }
}
