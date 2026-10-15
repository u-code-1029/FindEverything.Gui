namespace FindEverything.Desktop.Filtering;

/// <summary>One trailing-edge delay for search input, independent of scan progress and persistence.</summary>
internal static class SearchInputTiming
{
    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(700);
}
