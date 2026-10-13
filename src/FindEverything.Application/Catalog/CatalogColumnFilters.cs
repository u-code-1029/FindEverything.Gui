using System.Globalization;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

public enum CatalogColumnTargetKind { ProfileField, FullPath, Coverage }
public sealed record CatalogColumnTarget(CatalogColumnTargetKind Kind, string? FieldId = null)
{
    public static CatalogColumnTarget Field(string id) => new(CatalogColumnTargetKind.ProfileField, id.ToLowerInvariant());
    public static CatalogColumnTarget FullPath { get; } = new(CatalogColumnTargetKind.FullPath);
    public static CatalogColumnTarget Coverage { get; } = new(CatalogColumnTargetKind.Coverage);
}
public enum CatalogFilterOperator
{
    Contains, DoesNotContain, NumberRange, DateRange, BooleanEquals, CoverageEquals,
    IsMissing, HasValue, IsEmpty, IsNotEmpty
}
public sealed record CatalogColumnFilter(CatalogColumnTarget Target, CatalogFilterOperator Operator)
{
    public string? Text { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public bool? Boolean { get; init; }
}
public sealed record CatalogNumericRange(decimal Minimum, decimal Maximum);

/// <summary>Shared predicate semantics for a live scan and its persisted projection.</summary>
public static class CatalogColumnFilters
{
    public static ProfileFieldDescriptor? Validate(CatalogColumnFilter filter, IReadOnlyList<ProfileFieldDescriptor> fields)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(filter.Target);
        if (!Enum.IsDefined(filter.Target.Kind) || !Enum.IsDefined(filter.Operator))
            throw new ArgumentException("Unknown column filter.", nameof(filter));
        var field = filter.Target.Kind == CatalogColumnTargetKind.ProfileField
            ? fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, filter.Target.FieldId, StringComparison.OrdinalIgnoreCase)) : null;
        if (filter.Target.Kind == CatalogColumnTargetKind.ProfileField && field is null)
            throw new ArgumentException("The filter field is not defined by the profile.", nameof(filter));
        var kind = field?.Kind ?? (filter.Target.Kind == CatalogColumnTargetKind.Coverage ? ProfileFieldValueKind.Boolean : ProfileFieldValueKind.String);
        var valid = filter.Operator switch
        {
            CatalogFilterOperator.Contains or CatalogFilterOperator.DoesNotContain or CatalogFilterOperator.IsEmpty or CatalogFilterOperator.IsNotEmpty => kind == ProfileFieldValueKind.String,
            CatalogFilterOperator.NumberRange => kind is ProfileFieldValueKind.Int32 or ProfileFieldValueKind.Decimal,
            CatalogFilterOperator.DateRange => kind == ProfileFieldValueKind.DateTime,
            CatalogFilterOperator.BooleanEquals => field is not null && kind == ProfileFieldValueKind.Boolean && filter.Boolean.HasValue,
            CatalogFilterOperator.CoverageEquals => filter.Target.Kind == CatalogColumnTargetKind.Coverage && filter.Boolean.HasValue,
            CatalogFilterOperator.IsMissing or CatalogFilterOperator.HasValue => true,
            _ => false
        };
        if (!valid || filter.Minimum > filter.Maximum || filter.StartDate?.Date > filter.EndDate?.Date)
            throw new ArgumentException("The filter does not match the column type or its range is invalid.", nameof(filter));
        if (filter.Operator == CatalogFilterOperator.NumberRange && kind == ProfileFieldValueKind.Int32 &&
            ((filter.Minimum.HasValue && decimal.Truncate(filter.Minimum.Value) != filter.Minimum) ||
             (filter.Maximum.HasValue && decimal.Truncate(filter.Maximum.Value) != filter.Maximum)))
            throw new ArgumentException("Integer ranges require whole numbers.", nameof(filter));
        return field;
    }

    public static bool Matches(CatalogColumnFilter filter, object? rawValue, string? displayedValue)
    {
        if (filter.Operator == CatalogFilterOperator.IsMissing) return rawValue is null;
        if (filter.Operator == CatalogFilterOperator.HasValue) return rawValue is not null;
        if (rawValue is null) return false;
        return filter.Operator switch
        {
            CatalogFilterOperator.Contains => (displayedValue ?? string.Empty).ToLowerInvariant().Contains((filter.Text ?? string.Empty).ToLowerInvariant(), StringComparison.Ordinal),
            CatalogFilterOperator.DoesNotContain => !(displayedValue ?? string.Empty).ToLowerInvariant().Contains((filter.Text ?? string.Empty).ToLowerInvariant(), StringComparison.Ordinal),
            CatalogFilterOperator.IsEmpty => string.IsNullOrEmpty(displayedValue),
            CatalogFilterOperator.IsNotEmpty => !string.IsNullOrEmpty(displayedValue),
            CatalogFilterOperator.NumberRange when TryNumber(rawValue, out var number) =>
                (!filter.Minimum.HasValue || number >= filter.Minimum) && (!filter.Maximum.HasValue || number <= filter.Maximum),
            CatalogFilterOperator.DateRange when rawValue is DateTime date =>
                (!filter.StartDate.HasValue || date.Date >= filter.StartDate.Value.Date) && (!filter.EndDate.HasValue || date.Date <= filter.EndDate.Value.Date),
            CatalogFilterOperator.DateRange when rawValue is DateTimeOffset date =>
                (!filter.StartDate.HasValue || date.UtcDateTime.Date >= filter.StartDate.Value.Date) && (!filter.EndDate.HasValue || date.UtcDateTime.Date <= filter.EndDate.Value.Date),
            CatalogFilterOperator.BooleanEquals or CatalogFilterOperator.CoverageEquals => rawValue is bool value && value == filter.Boolean,
            _ => false
        };
    }

    private static bool TryNumber(object value, out decimal number)
    {
        switch (value)
        {
            case int integer: number = integer; return true;
            case long integer: number = integer; return true;
            case decimal precise: number = precise; return true;
            default: number = 0; return false;
        }
    }
}
