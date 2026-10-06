namespace FindEverything.Desktop.Filtering;

public static class TextFilter
{
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    public static IReadOnlyList<string> Terms(string? value) =>
        Normalize(value)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool Contains(string value, string normalizedTerm) =>
        value.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesAll(IEnumerable<string> values, string? query)
    {
        ArgumentNullException.ThrowIfNull(values);
        var searchable = values as IReadOnlyCollection<string> ?? values.ToArray();
        return Terms(query).All(term => searchable.Any(value => Contains(value, term)));
    }
}
