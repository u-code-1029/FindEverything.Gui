namespace FindEverything.Desktop.Filtering;

public static class TextFilter
{
    public static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    public static bool Contains(string value, string normalizedTerm) =>
        value.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase);
}
