using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private bool _hasExplicitResultSort;
    private TimeSpan? _completedLegacyScanElapsed;

    /// <summary>The scan that produced the displayed results, not a later query/load duration.</summary>
    public TimeSpan? LastCompletedScanElapsed => _displayedProjectionSnapshot is { } snapshot
        ? snapshot.Summary.ScanElapsed : _completedLegacyScanElapsed;

    public bool HasCompletedScanElapsed => LastCompletedScanElapsed.HasValue;

    public string LastCompletedScanElapsedText => LastCompletedScanElapsed is { } elapsed
        ? F("Loc.Catalog.Result.PersistentScanElapsed", "스캔 소요 {0}", FormatElapsed(elapsed)) : string.Empty;

    private void ResetResultPresentation()
    {
        _hasExplicitResultSort = false;
        _completedLegacyScanElapsed = null;
        OnPropertyChanged(nameof(ResultSourceDescription));
    }

    private void NotifyCompletedScanElapsed()
    {
        OnPropertyChanged(nameof(LastCompletedScanElapsed));
        OnPropertyChanged(nameof(HasCompletedScanElapsed));
        OnPropertyChanged(nameof(LastCompletedScanElapsedText));
    }

    private void EnsureDefaultResultSort(IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        // An explicit "clear sorting" is also a user choice. Only profile/data
        // source changes reset that choice, never a refresh or scan publication.
        if (_hasExplicitResultSort) return;
        var firstDate = fields.OrderBy(static field => field.Order)
            .FirstOrDefault(static field => field.Kind == ProfileFieldValueKind.DateTime);
        var target = firstDate is null ? null : CatalogColumnTarget.Field(firstDate.FieldId);
        var descending = firstDate is not null;
        if (_queryDefinition.SortTarget == target && _queryDefinition.SortFieldId is null
            && _queryDefinition.Descending == descending) return;
        _queryDefinition = _queryDefinition with { SortTarget = target, SortFieldId = null, Descending = descending };
        ColumnQueryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyLiveResultSort(System.Windows.Data.ListCollectionView view)
    {
        EnsureDefaultResultSort(Fields);
        var target = _queryDefinition.SortTarget;
        if (target is null && _queryDefinition.SortFieldId is { } legacyField)
            target = legacyField.Equals("FullPath", StringComparison.OrdinalIgnoreCase)
                ? CatalogColumnTarget.FullPath : CatalogColumnTarget.Field(legacyField);
        view.CustomSort = target is null ? null : new LiveColumnComparer(target, Fields, _queryDefinition.Descending);
    }
}
