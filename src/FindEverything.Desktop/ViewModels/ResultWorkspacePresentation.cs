using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    public bool ShowCancelAction => CanCancel();
    public bool HasPublishedResults => _displayedProjectionSnapshot is not null || HasLoadedItems;
    public bool CanTypeSearch => !IsScanning && !IsShowingSelectedOnly;
    public string ScanActionText => HasPublishedResults
        ? L("Loc.Ux.Results.Refresh", "다시 스캔") : L("Loc.Workspace.Scan", "스캔 시작");
    public string BusyProgressCaption => IsScanning && _displayedProjectionSnapshot is not null
        ? L("Loc.Ux.Results.PreviousRefreshing", "이전 결과 · 갱신 중") + " · " + ProgressMessage : ProgressMessage;
    public string ResultScopePath => _displayedProjectionSnapshot?.Scope.RootPath ?? string.Empty;
    public string ResultProvenance => _displayedProjectionSnapshot is { } snapshot
        ? ResultScopePath + " · " + F("Loc.Ux.Results.Source", "{0} · {1} 갱신", IsReadOnlyDataSource
            ? L("Loc.Ux.Results.Source.Shared", "가져온 결과") : L("Loc.Ux.Results.Source.Saved", "저장된 결과"),
            snapshot.Summary.UpdatedUtc.ToLocalTime().ToString("g", _localizer.Culture))
        : HasLoadedItems ? L("Loc.Ux.Results.Source.Preview", "스캔 미리보기") : string.Empty;
    public string ScanDisplayNotice => _displayedProjectionSnapshot is not null
        ? L("Loc.Ux.Results.Scan.Previous", "새 결과를 수집 중입니다. 현재 표에는 이전 결과가 표시됩니다. 완료되면 검색과 선택을 다시 사용할 수 있습니다.")
        : L("Loc.Ux.Results.Scan.Preview", "폴더를 확인하고 있습니다. 완료되면 전체 결과를 검색하고 선택할 수 있습니다.");
    public bool IsCatalogEmptyVisible => !IsBusy && !IsSearchPending && (!HasLoadedItems || Items.IsEmpty);
    public bool IsFilteredEmpty => !IsShowingSelectedOnly && HasLoadedItems && Items.IsEmpty;
    public bool CanTestEmptyProfile => !IsShowingSelectedOnly && !IsBusy && SelectedProfile is not null && !IsFilteredEmpty;
    public string EmptyResultTitle => IsShowingSelectedOnly
        ? L("Loc.Ux.Results.SelectedOnly.Empty", "선택한 항목이 없습니다")
        : IsFilteredEmpty
        ? L("Loc.Ux.Results.Empty.Filtered", "검색 조건에 맞는 항목이 없습니다")
        : StatusSeverity == InfoBarSeverity.Error ? L("Loc.Ux.Results.Empty.Error", "결과를 불러오지 못했습니다")
        : HasPublishedResults ? L("Loc.Ux.Results.Empty.Profile", "프로필 규칙에 맞는 폴더가 없습니다")
        : L("Loc.View.Catalog.Text.NoStructuredResultsLoadedYet", "아직 불러온 구조화 결과가 없습니다");
    public string EmptyResultHelp => IsShowingSelectedOnly
        ? L("Loc.Ux.Results.SelectedOnly.EmptyHelp", "선택 항목 검토를 닫으면 기존 검색 결과에서 다시 선택할 수 있습니다.")
        : IsFilteredEmpty
        ? L("Loc.Ux.Results.Empty.FilteredHelp", "검색어를 줄이거나 필터를 초기화하세요. 체크해 둔 항목은 유지됩니다.")
        : StatusSeverity == InfoBarSeverity.Error ? StatusMessage
        : HasPublishedResults ? L("Loc.Ux.Results.Empty.ProfileHelp", "탐색 로그에서 방문한 경로를 확인하고, 프로필 테스트에서 어떤 값이 추출되는지 확인하세요.")
        : L("Loc.Ux.Results.Empty.StartHelp", "프로필과 검색 위치를 선택한 뒤 스캔하세요. 결과는 자동으로 저장됩니다.");

    [RelayCommand]
    private void ResetResultFilters()
    {
        if (!CanEditResultFilters) return;
        FilterText = string.Empty;
        ClearColumnFiltersCommand.Execute(null);
        ApplySearchImmediately();
    }

    private static readonly PropertyChangedEventArgs[] SourcePresentationChanges =
        [new(nameof(HasPublishedResults)), new(nameof(ScanActionText)), new(nameof(ResultScopePath)), new(nameof(ResultProvenance)), new(nameof(ScanDisplayNotice))];
    private static readonly PropertyChangedEventArgs[] EmptyPresentationChanges =
        [new(nameof(IsCatalogEmptyVisible)), new(nameof(IsFilteredEmpty)), new(nameof(CanTestEmptyProfile)), new(nameof(EmptyResultTitle)), new(nameof(EmptyResultHelp))];
    private static readonly PropertyChangedEventArgs EmptyTitleChanged = new(nameof(EmptyResultTitle));
    private static readonly PropertyChangedEventArgs EmptyHelpChanged = new(nameof(EmptyResultHelp));
    private static readonly PropertyChangedEventArgs CanTypeSearchChanged = new(nameof(CanTypeSearch));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ProgressMessage) or nameof(IsScanning) or nameof(ResultSourceDescription) or nameof(HasLoadedItems))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(BusyProgressCaption)));
        if (e.PropertyName is nameof(CanEditWorkspace) or nameof(WorkspaceTransfer))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanImportProfile)));
            ImportProfileCommand.NotifyCanExecuteChanged();
        }
        if (e.PropertyName is nameof(IsBusy) or nameof(IsSearchPending) or nameof(IsPreservingSelection))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowCancelAction)));
        if (e.PropertyName is nameof(ResultSourceDescription) or nameof(HasLoadedItems) or nameof(IsReadOnlyDataSource))
            foreach (var change in SourcePresentationChanges) base.OnPropertyChanged(change);
        if (e.PropertyName is nameof(IsScanning) or nameof(IsShowingSelectedOnly)) base.OnPropertyChanged(CanTypeSearchChanged);
        if (e.PropertyName is nameof(HasLoadedItems) or nameof(IsBusy) or nameof(IsSearchPending)
            or nameof(FilterSummary) or nameof(Items) or nameof(SelectedProfile) or nameof(ResultSourceDescription) or nameof(IsShowingSelectedOnly))
            foreach (var change in EmptyPresentationChanges) base.OnPropertyChanged(change);
        else if (e.PropertyName is nameof(StatusSeverity) or nameof(StatusMessage))
        {
            base.OnPropertyChanged(EmptyTitleChanged);
            base.OnPropertyChanged(EmptyHelpChanged);
        }
    }
}

public partial class FileSearchViewModel
{
    public string EmptyResultTitle => IsShowingSelectedOnly
        ? L("Loc.Ux.Results.SelectedOnly.Empty", "선택한 항목이 없습니다")
        : L("Loc.View.Files.Text.NoMatchingItems", "검색 조건에 맞는 항목이 없습니다");
    public string EmptyResultHelp => IsShowingSelectedOnly
        ? L("Loc.Ux.Results.SelectedOnly.EmptyHelp", "선택 항목 검토를 닫으면 기존 검색 결과에서 다시 선택할 수 있습니다.")
        : L("Loc.View.Files.Text.TryAShorterQueryOrClearThe", "검색어를 줄이거나 검색 조건을 초기화하세요.");
    public bool ShowCancelAction => CanCancel();
    public string ScanActionText => HasIndex ? L("Loc.Ux.Results.Refresh", "결과 새로고침") : L("Loc.Workspace.Scan", "스캔 시작");
    public string ResultScopePath => _fileQueryScope?.RootPath ?? string.Empty;
    public string ResultProvenance => _fileQueryScope is null ? string.Empty
        : ResultScopePath + " · " + F("Loc.Ux.Results.Source", "{0} · {1} 갱신", IsReadOnlyDataSource
                ? L("Loc.Ux.Results.Source.Shared", "가져온 결과") : L("Loc.Ux.Results.Source.Saved", "저장된 결과"),
            _loadedSearchSnapshot?.LastPublishedUtc?.ToLocalTime().ToString("g", _localizer.Culture)
                ?? L("Loc.Common.TimeUnavailable", "시간 정보 없음"));
    public string ScanDisplayNotice => HasIndex
        ? L("Loc.Ux.Results.Scan.Previous", "새 결과를 수집 중입니다. 현재 표에는 이전 결과가 표시됩니다. 완료되면 검색과 선택을 다시 사용할 수 있습니다.")
        : L("Loc.Ux.Results.Scan.Preview", "폴더를 확인하고 있습니다. 완료되면 전체 결과를 검색하고 선택할 수 있습니다.");
    public string VisibleResultCount => !HasSearched ? L("Loc.Files.Results.Ready", "검색 준비")
        : IsResultCountPending || !_hasExactResultCount ? ResultSummary
        : F("Loc.Ux.Results.Count", "{0:N0}개 결과", TotalCount);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsResultInteractionEnabled) or nameof(IsShowingSelectedOnly))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanEditFileFilters)));
            ToggleFileFiltersCommand.NotifyCanExecuteChanged();
            if (IsShowingSelectedOnly) IsFilterOpen = false;
        }
        if (e.PropertyName is nameof(IsShowingSelectedOnly))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(EmptyResultTitle)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(EmptyResultHelp)));
        }
        if (e.PropertyName is nameof(SelectedKind) or nameof(MinimumSizeStep) or nameof(MaximumSizeStep)
            or nameof(SelectedCreatedDateFilter) or nameof(SelectedModifiedDateFilter))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActiveFileFilterCount)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FilterButtonText)));
        }
        if (e.PropertyName is nameof(IsBusy) or nameof(IsSearching) or nameof(IsResultCountPending) or nameof(IsPreservingSelection))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowCancelAction)));
        if (e.PropertyName is nameof(ResultSourceDescription) or nameof(HasIndex) or nameof(IsBusy) or nameof(IsReadOnlyDataSource))
            foreach (var name in new[] { nameof(ScanActionText), nameof(ResultScopePath), nameof(ResultProvenance), nameof(ScanDisplayNotice) })
                base.OnPropertyChanged(new PropertyChangedEventArgs(name));
        if (e.PropertyName is nameof(ResultSummary) or nameof(TotalCount) or nameof(IsResultCountPending) or nameof(HasSearched))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(VisibleResultCount)));
    }
}
