using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    [ObservableProperty] private bool _isSearchPending;
    [ObservableProperty] private bool _isPreservingSelection;
    private bool _selectionSpansFilters;
    private long? _currentSelectionCount = 0;
    private bool _isCurrentSelectionCountPending;
    private CancellationTokenSource? _selectionCountCancellation;
    private CancellationTokenSource? _bulkSelectionCancellation;
    private int _selectionPreparationCount;
    private void BeginSelectionPreservation() => IsPreservingSelection = ++_selectionPreparationCount > 0;
    private void EndSelectionPreservation() => IsPreservingSelection = --_selectionPreparationCount > 0;

    public bool? CurrentResultsSelectionState => _currentSelectionCount is null ? null
        : _currentSelectionCount == 0 ? false : _hasExactResultCount && _currentSelectionCount == (UsesQuerySelection ? _matchingCount : Items.Count) ? true : null;
    public string SelectionSummary => _currentSelectionCount.HasValue
        ? F("Loc.Ux.Results.SelectionSummary", "총 {0:N0}개 선택 · 현재 결과 {1:N0}개", SelectedCount, _currentSelectionCount.Value)
        : _isCurrentSelectionCountPending
            ? F("Loc.Ux.Results.SelectionCounting", "총 {0:N0}개 선택 · 현재 결과 확인 중", SelectedCount)
            : F("Loc.Ux.Results.SelectionCountUnknown", "총 {0:N0}개 선택 · 현재 결과 선택 수 미확인", SelectedCount);

    public void ApplySearchImmediately()
    {
        _liveInputRequests.Cancel();
        if (_projectionSnapshot is not null && ActiveProjectionStore is not null) _ = RefreshProjectionQueryAsync();
        else { ApplyFilter(); IsSearchPending = false; }
    }

    partial void OnIsSearchPendingChanged(bool value)
    {
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        ToggleCurrentResultsSelectionCommand.NotifyCanExecuteChanged();
        NotifySelectionImportAvailability();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OpenSelectedCommand.NotifyCanExecuteChanged();
        OpenSelectedFolderCommand.NotifyCanExecuteChanged();
        CopySelectedFolderPathCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPreservingSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsResultInteractionEnabled));
        OnPropertyChanged(nameof(CanEditWorkspace));
        OnIsScanningChanged(IsScanning);
        ScanCommand.NotifyCanExecuteChanged(); LoadCommand.NotifyCanExecuteChanged();
        RefreshTextColumnCommand.NotifyCanExecuteChanged();
        NotifySelectionImportAvailability();
        CancelCommand.NotifyCanExecuteChanged();
        ToggleCurrentResultsSelectionCommand.NotifyCanExecuteChanged();
        if (value) ProgressMessage = L("Loc.Ux.Results.PreservingSelection", "선택 범위 보존 중… 검색 조건은 변경할 수 있습니다.");
    }

    private void SetCurrentSelectionCount(long? value)
    {
        _currentSelectionCount = value;
        OnPropertyChanged(nameof(CurrentResultsSelectionState));
        OnPropertyChanged(nameof(SelectionSummary));
        ToggleCurrentResultsSelectionCommand.NotifyCanExecuteChanged();
    }

    private async Task PreserveSelectionScopeAsync(CatalogProjectionSnapshot snapshot,
        CatalogProjectionQuery definition, CancellationToken token)
    {
        if (_displayedProjectionSnapshot?.Generation != snapshot.Generation
            || _displayedProjectionSnapshot.Scope != snapshot.Scope) return;
        if (_displayedQueryDefinition.HasSamePredicates(definition)) return;
        if (_querySelection.Count(long.MaxValue) == 0) return;
        if (_querySelection.AllMatching && _projectionQuery is { } previous)
        {
            BeginSelectionPreservation();
            try
            {
                using var captured = _querySelection.Capture();
                using var keys = await SqliteSelectionSnapshot.CreateAsync(previous, captured,
                    row => row.StableKey ?? row.FullPath, row => row.FullPath, token);
                token.ThrowIfCancellationRequested();
                _querySelection.ReplaceBaseline(keys);
            }
            finally { EndSelectionPreservation(); }
        }
        _selectionSpansFilters = true;
        SetCurrentSelectionCount(null);
    }

    private async Task RefreshCurrentSelectionCountAsync()
    {
        _selectionCountCancellation?.Cancel();
        _selectionCountCancellation = null;
        _isCurrentSelectionCountPending = false;
        if (_projectionQuery is not { } query || SelectedCount == 0) { SetCurrentSelectionCount(0); return; }
        if (!_selectionSpansFilters) { SetCurrentSelectionCount(SelectedCount); return; }
        using var cancellation = new CancellationTokenSource();
        _selectionCountCancellation = cancellation;
        _isCurrentSelectionCountPending = true;
        using var selection = _querySelection.Capture();
        SetCurrentSelectionCount(null);
        try
        {
            var count = await Task.Run(() => query.CountSelectionAsync(selection, cancellation.Token), cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(query, _projectionQuery)) SetCurrentSelectionCount(count);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or ResultQueryChangedException) { }
        catch (Exception exception) { _logger.LogDebug(exception, "Could not count checked rows in the current catalog filter."); }
        finally
        {
            if (ReferenceEquals(_selectionCountCancellation, cancellation))
            {
                _selectionCountCancellation = null;
                _isCurrentSelectionCountPending = false;
                OnPropertyChanged(nameof(SelectionSummary));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllResults))]
    private void ToggleCurrentResultsSelection()
    {
        if (CurrentResultsSelectionState == true)
        {
            if (!UsesQuerySelection) { SetSelection(_selectedItems.Where(item => !Items.Contains(item)).ToArray()); return; }
            if (_selectionSpansFilters) _ = ChangeCurrentSelectionAsync(SqliteSelectionSnapshot.SetOperation.Except);
            else ClearSelection();
        }
        else SelectAll();
    }

    private async Task ChangeCurrentSelectionAsync(SqliteSelectionSnapshot.SetOperation operation)
    {
        if (_projectionQuery is not { } query || !IsResultInteractionEnabled) return;
        using var cancellation = new CancellationTokenSource();
        _bulkSelectionCancellation = cancellation;
        BeginSelectionPreservation();
        CancelSelectionRequests();
        using var captured = _querySelection.Capture();
        using var all = new QuerySelection(true);
        try
        {
            using var current = await SqliteSelectionSnapshot.CreateAsync(query, all,
                row => row.StableKey ?? row.FullPath, row => row.FullPath, cancellation.Token);
            var source = WorkspaceTransfer?.CurrentSource;
            using var merged = await SqliteSelectionSnapshot.CombineAsync(captured, current, operation,
                cancellation.Token, source is null ? null : key => WorkspacePathMapping.ToEffective(source, key));
            if (cancellation.IsCancellationRequested || !ReferenceEquals(query, _projectionQuery)) return;
            _querySelection.ReplaceBaseline(merged);
            UpdateProjectionSelection();
            await RefreshCurrentSelectionCountAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SetStatus(L("Loc.Common.Error", "오류"), exception.Message, Wpf.Ui.Controls.InfoBarSeverity.Error); }
        finally
        {
            if (ReferenceEquals(_bulkSelectionCancellation, cancellation)) _bulkSelectionCancellation = null;
            EndSelectionPreservation();
        }
    }
}
