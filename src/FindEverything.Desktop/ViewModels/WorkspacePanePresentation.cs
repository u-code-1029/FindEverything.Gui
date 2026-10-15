using CommunityToolkit.Mvvm.Input;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    [RelayCommand]
    private void CloseDetails() => IsDetailsOpen = false;
}

public partial class FileSearchViewModel
{
    public bool CanEditFileFilters => IsResultInteractionEnabled && !IsShowingSelectedOnly;
    public int ActiveFileFilterCount => (SelectedKind?.Value is null ? 0 : 1)
        + (MinimumSizeStep > 0 || MaximumSizeStep < UnlimitedSizeStep ? 1 : 0)
        + (SelectedCreatedDateFilter?.Value is null or DateFilterPreset.Any ? 0 : 1)
        + (SelectedModifiedDateFilter?.Value is null or DateFilterPreset.Any ? 0 : 1);
    public string FilterButtonText => ActiveFileFilterCount == 0
        ? L("Loc.Ux.Files.Filters", "검색 조건")
        : F("Loc.Ux.Files.Filters.Count", "검색 조건 ({0})", ActiveFileFilterCount);

    partial void OnIsDetailsOpenChanged(bool value)
    {
        if (value) IsFilterOpen = false;
    }

    partial void OnIsFilterOpenChanged(bool value)
    {
        if (value) IsDetailsOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanEditFileFilters))]
    private void ToggleFileFilters() => IsFilterOpen = !IsFilterOpen;

    [RelayCommand]
    private void CloseFileFilters() => IsFilterOpen = false;

    [RelayCommand]
    private void CloseDetails() => IsDetailsOpen = false;
}
