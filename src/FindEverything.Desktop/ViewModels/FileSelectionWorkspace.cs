using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Indexing;
using FindEverything.Application.FileSearch;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.ViewModels;

public partial class FileSearchViewModel
{
    [ObservableProperty] private bool _isPreservingSelection;
    private bool _selectionSpansFilters;
    private long? _currentSelectionCount = 0;
    private bool _isCurrentSelectionCountPending;
    private CancellationTokenSource? _selectionCountCancellation;
    private CancellationTokenSource? _bulkSelectionCancellation;
    private long _selectionFlagsVersion;
    private int _selectionPreparationCount;
    private void BeginSelectionPreservation() => IsPreservingSelection = ++_selectionPreparationCount > 0;
    private void EndSelectionPreservation() => IsPreservingSelection = --_selectionPreparationCount > 0;

    public bool? CurrentResultsSelectionState => _currentSelectionCount is null ? null
        : _currentSelectionCount == 0 ? false : _hasExactResultCount && _currentSelectionCount == TotalCount ? true : null;
    public string SelectionSummary => _currentSelectionCount.HasValue
        ? F("Loc.Ux.Results.SelectionSummary", "총 {0:N0}개 선택 · 현재 결과 {1:N0}개", SelectedCount, _currentSelectionCount.Value)
        : _isCurrentSelectionCountPending
            ? F("Loc.Ux.Results.SelectionCounting", "총 {0:N0}개 선택 · 현재 결과 확인 중", SelectedCount)
            : F("Loc.Ux.Results.SelectionCountUnknown", "총 {0:N0}개 선택 · 현재 결과 선택 수 미확인", SelectedCount);
    public void ApplySearchImmediately() => _ = StartLoadAsync(refreshStatus: false, debounce: false, preserveSelection: true);
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SelectionSummary));
    partial void OnIsPreservingSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsResultInteractionEnabled));
        CopySelectionCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged(); InvertSelectionCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged(); ToggleCurrentResultsSelectionCommand.NotifyCanExecuteChanged();
        RefreshIndexCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        BrowseRefreshScopeCommand.NotifyCanExecuteChanged(); RefreshScopeCommand.NotifyCanExecuteChanged();
        RefreshItemScopeCommand.NotifyCanExecuteChanged();
        BrowseRootCommand.NotifyCanExecuteChanged(); BrowseDatabaseCommand.NotifyCanExecuteChanged();
        UseDefaultDatabasePathCommand.NotifyCanExecuteChanged();
        if (value) ProgressMessage = L("Loc.Ux.Results.PreservingSelection", "선택 범위 보존 중… 검색 조건은 변경할 수 있습니다.");
    }
    private void SetCurrentSelectionCount(long? value)
    {
        _currentSelectionCount = value;
        OnPropertyChanged(nameof(CurrentResultsSelectionState)); OnPropertyChanged(nameof(SelectionSummary));
        ToggleCurrentResultsSelectionCommand.NotifyCanExecuteChanged();
    }
    private static EntrySearchRequest WithoutResultFilters(EntrySearchRequest request) => request with
    {
        Keyword = null, Kind = null, MinSizeBytes = null, MaxSizeBytes = null,
        CreatedFromUtc = null, CreatedBeforeUtc = null, ModifiedFromUtc = null, ModifiedBeforeUtc = null,
        FullPaths = [], Offset = 0, AfterCursor = null
    };
    private async Task PreserveSelectionScopeAsync(FileSearchScope scope, EntrySearchRequest request, CancellationToken token)
    {
        if (_fileQueryScope != scope || _fileQueryRequest is not { } old
            || old.ExpectedScanId != request.ExpectedScanId || SameResultFilters(old, request)) return;
        if (_selection.Count(long.MaxValue) == 0) return;
        if (_selection.AllMatching && _fileQuery is { } previous)
        {
            BeginSelectionPreservation();
            try
            {
                using var captured = _selection.Capture();
                using var keys = await SqliteSelectionSnapshot.CreateAsync(previous, captured,
                    row => row.StableKey ?? row.FullPath, row => row.FullPath, token);
                token.ThrowIfCancellationRequested();
                _selection.ReplaceBaseline(keys);
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
        if (_fileQuery is not { } query || SelectedCount == 0) { SetCurrentSelectionCount(0); return; }
        if (!_selectionSpansFilters) { SetCurrentSelectionCount(SelectedCount); return; }
        using var cancellation = new CancellationTokenSource(); _selectionCountCancellation = cancellation;
        _isCurrentSelectionCountPending = true;
        using var captured = _selection.Capture(); SetCurrentSelectionCount(null);
        try
        {
            var count = await Task.Run(() => query.CountSelectionAsync(captured, cancellation.Token), cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(query, _fileQuery)) SetCurrentSelectionCount(count);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IndexSnapshotChangedException) { }
        catch (Exception exception) { _logger.LogDebug(exception, "Could not count checked files in the current filter."); }
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
    private async Task UpdateSelectionFlagsAsync(IReadOnlyList<FileSearchItemViewModel> items)
    {
        var query = _fileQuery; var version = _selectionFlagsVersion;
        using var captured = _selection.Capture();
        try
        {
            var flags = await captured.ReadMembershipAsync(items.Select(item => item.SelectionKey).ToArray());
            if (_disposed || !ReferenceEquals(query, _fileQuery) || version != _selectionFlagsVersion) return;
            for (var i = 0; i < items.Count; i++) items[i].IsSelected = flags[i];
            OpenSelectedCommand.NotifyCanExecuteChanged(); OpenSelectedFolderCommand.NotifyCanExecuteChanged();
            ShowSelectedInFolderCommand.NotifyCanExecuteChanged(); CopySelectedPathCommand.NotifyCanExecuteChanged();
        }
        catch (ObjectDisposedException) { }
    }
    [RelayCommand(CanExecute = nameof(CanSelectAllResults))]
    private void ToggleCurrentResultsSelection()
    {
        if (CurrentResultsSelectionState == true)
        {
            if (_selectionSpansFilters) _ = ChangeCurrentSelectionAsync(SqliteSelectionSnapshot.SetOperation.Except);
            else ClearSelection();
        }
        else SelectAll();
    }
    private async Task ChangeCurrentSelectionAsync(SqliteSelectionSnapshot.SetOperation operation)
    {
        if (_fileQuery is not { } query || !IsResultInteractionEnabled) return;
        using var cancellation = new CancellationTokenSource(); _bulkSelectionCancellation = cancellation;
        BeginSelectionPreservation(); _selectionController?.Cancel();
        using var captured = _selection.Capture(); using var all = new QuerySelection(true);
        try
        {
            using var current = await SqliteSelectionSnapshot.CreateAsync(query, all,
                row => row.StableKey ?? row.FullPath, row => row.FullPath, cancellation.Token);
            var source = WorkspaceTransfer?.CurrentSource;
            using var merged = await SqliteSelectionSnapshot.CombineAsync(captured, current, operation,
                cancellation.Token, source is null ? null : key => WorkspacePathMapping.ToEffective(source, key));
            if (cancellation.IsCancellationRequested || !ReferenceEquals(query, _fileQuery)) return;
            _selectionFlagsVersion++;
            _selection.ReplaceBaseline(merged); UpdateQuerySelection();
            await RefreshCurrentSelectionCountAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus(L("Loc.Common.Error", "오류"), exception.Message, Wpf.Ui.Controls.InfoBarSeverity.Error); }
        finally
        {
            if (ReferenceEquals(_bulkSelectionCancellation, cancellation)) _bulkSelectionCancellation = null;
            EndSelectionPreservation();
        }
    }
}
