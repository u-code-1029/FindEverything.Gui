using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilePlaygroundViewModel
{
    private MappedProfileItem? _selectionSampleItem;
    private CancellationTokenSource? _selectionTestCancellation;
    [ObservableProperty] private ProfileSelectionMatchRuleManifest? _selectedSelectionRule;
    [ObservableProperty] private string _selectionInputText = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _selectionPreviewRows = [];
    [ObservableProperty] private string _selectionTestSummary = string.Empty;
    public IReadOnlyList<ProfileSelectionMatchRuleManifest> SelectionRules => SelectedProfile?.Profile.Descriptor.SelectionMatchRules ?? [];
    private bool CanPreviewSelectionRule() => !IsBusy && SelectedSelectionRule is not null && !string.IsNullOrWhiteSpace(SelectionInputText);
    partial void OnSelectedSelectionRuleChanged(ProfileSelectionMatchRuleManifest? value)
    { ClearSelectionTest(); PreviewSelectionRuleCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectionInputTextChanged(string value)
    { ClearSelectionTest(); PreviewSelectionRuleCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) => PreviewSelectionRuleCommand.NotifyCanExecuteChanged();
    private void ClearSelectionTest()
    { _selectionTestCancellation?.Cancel(); SelectionPreviewRows = []; SelectionTestSummary = string.Empty; }
    [RelayCommand(CanExecute = nameof(CanPreviewSelectionRule))]
    private async Task PreviewSelectionRuleAsync()
    {
        if (SelectedSelectionRule is not { } rule || SelectedProfile is not { } profile) return;
        using var cancellation = new CancellationTokenSource(); _selectionTestCancellation = cancellation;
        var revision = Volatile.Read(ref _evaluationGeneration); IsBusy = true;
        try
        {
            var result = await ProfileSelectionPlayground.TestAsync(profile.Profile, _selectionSampleItem, rule,
                SelectionInputText, _localizer.Culture.Name, cancellation.Token).ConfigureAwait(true);
            if (revision != Volatile.Read(ref _evaluationGeneration) || cancellation.IsCancellationRequested) return;
            SelectionPreviewRows = result.PreviewRows;
            SelectionTestSummary = result.MatchSummary is { } summary
                ? _localizer.Format("Loc.Profiles.Features.Playground.Matched", "입력 {0:N0}행 · 유효 {1:N0}행 · 샘플 일치 {2:N0}개 · 오류 {3:N0}행",
                    summary.InputRows, summary.ValidRows, summary.MatchingItems, summary.InvalidRows)
                    + (summary.Issues.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, summary.Issues.Take(10).Select(issue => issue.Message)) : string.Empty)
                : _localizer.Format("Loc.Profiles.Features.Playground.Parsed", "입력 {0:N0}행을 해석했습니다. 실제 경로를 먼저 테스트하면 그 한 행과의 일치 여부도 확인합니다.", result.ParsedRows);
            if (rule.Columns.Any(column => !string.IsNullOrWhiteSpace(column.RegexPattern)))
                SelectionTestSummary = _localizer.Get("Loc.SelectionImport.Regex.PassedRows", "정규식 조건을 모두 통과한 행 기준 · ") + SelectionTestSummary;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { SelectionTestSummary = UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception); }
        finally { if (ReferenceEquals(_selectionTestCancellation, cancellation)) _selectionTestCancellation = null; IsBusy = false; }
    }
}
