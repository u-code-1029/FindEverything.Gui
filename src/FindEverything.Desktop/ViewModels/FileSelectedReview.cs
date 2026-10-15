using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Results;

namespace FindEverything.Desktop.ViewModels;

public partial class FileSearchViewModel
{
    private bool _isShowingSelectedOnly;
    private bool _displayedSelectedOnly;
    private bool _selectedReviewRefreshPending;
    private readonly LatestRequestController _selectedReviewRequests = new();
    public bool IsShowingSelectedOnly => _isShowingSelectedOnly;
    public bool CanShowSelectedOnly => !_disposed && IsResultInteractionEnabled && !IsSearching
        && (IsShowingSelectedOnly || SelectedCount > 0);
    public bool CanEditResultFilters => IsResultInteractionEnabled && !IsShowingSelectedOnly;
    public string SelectedOnlyDescription => IsSearching || IsPreservingSelection
        ? L("Loc.Ux.Results.PreparingSelectedOnly", "선택 항목 검토를 준비하는 중… 취소할 수 있습니다.")
        : L("Loc.Ux.Results.SelectedOnly.Description",
        "전체 선택 항목을 표시합니다. 검색 조건은 잠시 적용하지 않으며, 검토를 닫으면 복원됩니다.");

    private void NotifySelectedReviewAvailability()
    {
        OnPropertyChanged(nameof(CanShowSelectedOnly));
        OnPropertyChanged(nameof(CanEditResultFilters));
        OnPropertyChanged(nameof(SelectedOnlyDescription));
        OnPropertyChanged(nameof(IsResultInteractionEnabled));
        ToggleSelectedOnlyCommand.NotifyCanExecuteChanged();
        ClearFiltersCommand.NotifyCanExecuteChanged();
        ClearSearchAndFiltersCommand.NotifyCanExecuteChanged();
    }

    private void SetSelectedOnlyMode(bool value)
    {
        if (_isShowingSelectedOnly == value) return;
        _isShowingSelectedOnly = value;
        if (!value) _selectedReviewRefreshPending = false;
        OnPropertyChanged(nameof(IsShowingSelectedOnly));
        OnPropertyChanged(nameof(SelectedOnlyDescription));
        NotifySelectedReviewAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanShowSelectedOnly))]
    private async Task ToggleSelectedOnlyAsync()
    {
        if (!CanShowSelectedOnly) return;
        _selectedReviewRequests.Cancel();
        SetSelectedOnlyMode(!IsShowingSelectedOnly);
        var loading = StartLoadAsync(refreshStatus: false, debounce: false, preserveSelection: true);
        var version = _queryVersion;
        await loading;
        if (!_disposed && version == _queryVersion) SetSelectedOnlyMode(_displayedSelectedOnly);
    }

    private void ScheduleSelectedReviewRefresh()
    {
        if (!IsShowingSelectedOnly || _disposed) return;
        _selectedReviewRefreshPending = true;
        if (!_isActive) return;
        _ = _selectedReviewRequests.RunAsync(TimeSpan.FromMilliseconds(150), async token =>
        {
            if (_disposed || !_isActive || !IsShowingSelectedOnly) return;
            await StartLoadAsync(refreshStatus: false, debounce: false, preserveSelection: true, cancellationToken: token);
        });
    }
}
