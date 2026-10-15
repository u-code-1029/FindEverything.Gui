using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public sealed record CatalogActiveColumnFilter(CatalogColumnFilter Filter, string Description, IRelayCommand RemoveCommand);

public partial class CatalogViewModel
{
    public ObservableCollection<CatalogActiveColumnFilter> ActiveColumnFilters { get; } = [];
    public bool HasColumnFilters => ActiveColumnFilters.Count > 0;
    public event EventHandler? ColumnQueryChanged;

    public CatalogColumnFilter? GetColumnFilter(CatalogColumnTarget target) => _queryDefinition.Filters.FirstOrDefault(filter => filter.Target == target);
    public bool IsColumnSorted(CatalogColumnTarget target) => _queryDefinition.SortTarget == target;
    public bool IsColumnSortDescending => _queryDefinition.Descending;

    public void ApplyColumnFilter(CatalogColumnFilter filter, bool debounce = false)
    {
        if (!CanEditResultFilters) return;
        CatalogColumnFilters.Validate(filter, Fields);
        var next = _queryDefinition.Filters.Where(existing => existing.Target != filter.Target).Append(filter)
            .OrderBy(existing => existing.Target.Kind).ThenBy(existing => existing.Target.FieldId, StringComparer.Ordinal).ToArray();
        if (next.SequenceEqual(_queryDefinition.Filters)) return;
        _queryDefinition = _queryDefinition with { Filters = next };
        RebuildActiveColumnFilters();
        // Continuous popup input shares the keyword debounce. Explicit API
        // calls remain immediate, as do sorting and removing a condition.
        if (debounce) ScheduleTextFilter();
        else ApplyFilter();
    }

    public void ClearColumnFilter(CatalogColumnTarget target)
    {
        if (!CanEditResultFilters) return;
        var next = _queryDefinition.Filters.Where(existing => existing.Target != target).ToArray();
        if (next.Length == _queryDefinition.Filters.Count) return;
        _queryDefinition = _queryDefinition with { Filters = next };
        RebuildActiveColumnFilters();
        ApplyFilter();
    }

    [RelayCommand(CanExecute = nameof(CanEditResultFilters))]
    private void ClearColumnFilters()
    {
        if (!CanEditResultFilters) return;
        if (_queryDefinition.Filters.Count == 0) return;
        _queryDefinition = _queryDefinition with { Filters = [] };
        RebuildActiveColumnFilters();
        ApplyFilter();
    }

    public void ApplyColumnSort(CatalogColumnTarget target, bool? descending)
    {
        if (!IsResultInteractionEnabled) return;
        _hasExplicitResultSort = true;
        _queryDefinition = _queryDefinition with { SortTarget = descending.HasValue ? target : null,
            SortFieldId = null, Descending = descending ?? false };
        ColumnQueryChanged?.Invoke(this, EventArgs.Empty);
        if (_projectionSnapshot is not null) _ = RefreshProjectionQueryAsync(preserveSelection: true);
        else
        {
            Items.CustomSort = descending.HasValue ? new LiveColumnComparer(target, Fields, descending.Value) : null;
            Items.Refresh();
        }
    }

    public async Task<CatalogNumericRange?> ReadColumnNumericRangeAsync(string fieldId, CancellationToken token)
    {
        if (_projectionSnapshot is { } snapshot && ActiveProjectionStore is { } store)
            return await store.ReadNumericRangeAsync(snapshot, fieldId, token);
        var numbers = _loadedItems.Select(item => item.Values.GetValueOrDefault(fieldId)).OfType<IConvertible>()
            .Where(value => value is int or long or decimal).Select(value => Convert.ToDecimal(value)).ToArray();
        return numbers.Length == 0 ? null : new(numbers.Min(), numbers.Max());
    }

    private void RebuildActiveColumnFilters()
    {
        ActiveColumnFilters.Clear();
        foreach (var filter in _queryDefinition.Filters)
        {
            var header = filter.Target.Kind switch
            {
                CatalogColumnTargetKind.FullPath => L("Loc.Catalog.Column.FolderPath", "폴더 경로"),
                CatalogColumnTargetKind.Coverage => L("Loc.Catalog.Column.Status", "상태"),
                _ => Fields.FirstOrDefault(field => string.Equals(field.FieldId, filter.Target.FieldId, StringComparison.OrdinalIgnoreCase))?.Header ?? filter.Target.FieldId ?? ""
            };
            var detail = filter.Operator switch
            {
                CatalogFilterOperator.Contains => F("Loc.Catalog.ColumnFilter.ContainsValue", "‘{0}’ 포함", filter.Text),
                CatalogFilterOperator.DoesNotContain => F("Loc.Catalog.ColumnFilter.NotContainsValue", "‘{0}’ 미포함", filter.Text),
                CatalogFilterOperator.NumberRange => $"{filter.Minimum?.ToString(_localizer.Culture) ?? "−∞"} – {filter.Maximum?.ToString(_localizer.Culture) ?? "+∞"}",
                CatalogFilterOperator.DateRange => $"{filter.StartDate?.ToString("d", _localizer.Culture) ?? "…"} – {filter.EndDate?.ToString("d", _localizer.Culture) ?? "…"}",
                CatalogFilterOperator.CoverageEquals => filter.Boolean == true ? L("Loc.Common.PendingScope", "대기 범위") : L("Loc.Common.Completed", "완료"),
                CatalogFilterOperator.BooleanEquals => filter.Boolean == true ? L("Loc.Catalog.ColumnFilter.True", "참") : L("Loc.Catalog.ColumnFilter.False", "거짓"),
                CatalogFilterOperator.IsMissing => L("Loc.Catalog.ColumnFilter.Missing", "값 없음"),
                CatalogFilterOperator.HasValue => L("Loc.Catalog.ColumnFilter.HasValue", "값 있음"),
                CatalogFilterOperator.IsEmpty => L("Loc.Catalog.ColumnFilter.Empty", "빈 텍스트"),
                _ => L("Loc.Catalog.ColumnFilter.NotEmpty", "비어 있지 않은 텍스트")
            };
            ActiveColumnFilters.Add(new(filter, header + ": " + detail, new RelayCommand(() => ClearColumnFilter(filter.Target))));
        }
        OnPropertyChanged(nameof(HasColumnFilters));
        ColumnQueryChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool MatchesColumnFilters(CatalogItemViewModel item)
    {
        foreach (var filter in _queryDefinition.Filters)
        {
            object? raw;
            string? display;
            if (filter.Target.Kind == CatalogColumnTargetKind.FullPath) raw = display = item.FullPath;
            else if (filter.Target.Kind == CatalogColumnTargetKind.Coverage) { raw = item.CoveragePending; display = item.CoverageText; }
            else
            {
                var fieldId = filter.Target.FieldId!;
                raw = item.UserInputValues.TryGetValue(fieldId, out var input) ? input.Value : item.Values.GetValueOrDefault(fieldId);
                display = item.CanonicalDisplayValues.GetValueOrDefault(fieldId);
            }
            if (!CatalogColumnFilters.Matches(filter, raw, display)) return false;
        }
        return true;
    }

    private sealed class LiveColumnComparer(CatalogColumnTarget target, IReadOnlyList<ProfileFieldDescriptor> fields, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not CatalogItemViewModel left || y is not CatalogItemViewModel right) return 0;
            object? Value(CatalogItemViewModel row) => target.Kind switch
            {
                CatalogColumnTargetKind.FullPath => row.FullPath,
                CatalogColumnTargetKind.Coverage => row.CoveragePending,
                _ => fields.FirstOrDefault(field => string.Equals(field.FieldId, target.FieldId, StringComparison.OrdinalIgnoreCase))?.Kind == ProfileFieldValueKind.String
                    ? row.CanonicalDisplayValues.GetValueOrDefault(target.FieldId!) : row.Values.GetValueOrDefault(target.FieldId!)
            };
            var a = Value(left); var b = Value(right);
            if (a is null || b is null) return a is null ? b is null ? 0 : 1 : -1;
            var comparison = a is string text ? string.Compare(text.ToLowerInvariant(), (b as string)?.ToLowerInvariant(), StringComparison.Ordinal)
                : a is IComparable comparable ? comparable.CompareTo(b) : 0;
            return comparison != 0 ? descending ? -comparison : comparison : string.Compare(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
