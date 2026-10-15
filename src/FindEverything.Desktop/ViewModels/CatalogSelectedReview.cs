using CommunityToolkit.Mvvm.Input;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private bool _isShowingSelectedOnly;
    private bool _displayedSelectedOnly;
    private bool _selectedReviewRefreshPending;
    public bool IsShowingSelectedOnly => _isShowingSelectedOnly;
    public bool CanShowSelectedOnly => !_disposed && IsResultInteractionEnabled && !IsSearchPending
        && (IsShowingSelectedOnly || SelectedCount > 0);
    public bool CanEditResultFilters => IsResultInteractionEnabled && !IsShowingSelectedOnly;
    public string SelectedOnlyDescription => IsSearchPending || IsPreservingSelection
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
        ClearColumnFiltersCommand.NotifyCanExecuteChanged();
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
        _liveInputRequests.Cancel();
        SetSelectedOnlyMode(!IsShowingSelectedOnly);
        if (_projectionSnapshot is not null)
        {
            var loading = RefreshProjectionQueryAsync(preserveSelection: true);
            var version = _queryVersion;
            await loading;
            if (!_disposed && version == _queryVersion) SetSelectedOnlyMode(_displayedSelectedOnly);
        }
        else
        {
            AppliedHighlightQuery = IsShowingSelectedOnly ? string.Empty : FilterText;
            Items.Refresh();
        }
    }

    private void ScheduleSelectedReviewRefresh()
    {
        if (!IsShowingSelectedOnly || _disposed) return;
        _selectedReviewRefreshPending = true;
        if (!_isActive) return;
        _ = _liveInputRequests.RunAsync(TimeSpan.FromMilliseconds(150), RefreshSelectedReviewAsync);
    }

    private async Task RefreshSelectedReviewAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !_isActive || !IsShowingSelectedOnly) return;
        if (_projectionSnapshot is not null)
            await RefreshProjectionQueryAsync(preserveSelection: true, cancellationToken: cancellationToken);
        else
        {
            Items.Refresh();
            _selectedReviewRefreshPending = false;
        }
    }
}
