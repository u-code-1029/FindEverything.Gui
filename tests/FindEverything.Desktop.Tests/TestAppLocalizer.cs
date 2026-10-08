using System.Globalization;
using FindEverything.Desktop.Localization;

namespace FindEverything.Desktop.Tests;

internal sealed class TestAppLocalizer(
    string cultureName = "ko-KR",
    IReadOnlyDictionary<string, string>? resources = null) : IAppLocalizer
{
    public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(cultureName);

    public string Get(string key, string koreanFallback) =>
        resources is not null && resources.TryGetValue(key, out var value)
            ? value
            : koreanFallback;

    public string Format(
        string key,
        string koreanFallback,
        params object?[] arguments) =>
        string.Format(Culture, Get(key, koreanFallback), arguments);
}
