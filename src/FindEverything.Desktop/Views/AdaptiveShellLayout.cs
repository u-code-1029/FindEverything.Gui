namespace FindEverything.Desktop.Views;

internal enum ShellLayoutMode
{
    Expanded,
    Compact,
}

internal readonly record struct ShellLayoutState(
    ShellLayoutMode Mode,
    bool ShowApplicationTitle);

/// <summary>
/// Keeps the application shell usable when the window is narrower than the
/// content-oriented desktop layout. The thresholds live outside code-behind so
/// the responsive contract can be regression tested without showing a window.
/// </summary>
internal static class AdaptiveShellLayout
{
    internal const double MinimumSupportedWidth = 760;
    internal const double ExpandedNavigationBreakpoint = 1080;
    internal const double VisibleApplicationTitleBreakpoint = 840;

    internal static ShellLayoutState Resolve(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            width = MinimumSupportedWidth;
        }

        return new ShellLayoutState(
            width >= ExpandedNavigationBreakpoint
                ? ShellLayoutMode.Expanded
                : ShellLayoutMode.Compact,
            width >= VisibleApplicationTitleBreakpoint);
    }
}
