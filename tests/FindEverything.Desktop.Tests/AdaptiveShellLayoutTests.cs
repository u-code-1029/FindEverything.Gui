using FindEverything.Desktop.Views;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class AdaptiveShellLayoutTests
{
    [Theory]
    [InlineData(760)]
    [InlineData(900)]
    [InlineData(1079.99)]
    public void Resolve_UsesCompactNavigationWhenContentWidthIsLimited(double width)
    {
        var state = AdaptiveShellLayout.Resolve(width);

        Assert.Equal(ShellLayoutMode.Compact, state.Mode);
    }

    [Theory]
    [InlineData(1080)]
    [InlineData(1360)]
    public void Resolve_UsesExpandedNavigationAtDesktopWidths(double width)
    {
        var state = AdaptiveShellLayout.Resolve(width);

        Assert.Equal(ShellLayoutMode.Expanded, state.Mode);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    public void Resolve_UsesSafeNarrowLayoutForInvalidMeasurement(double width)
    {
        var state = AdaptiveShellLayout.Resolve(width);

        Assert.Equal(ShellLayoutMode.Compact, state.Mode);
        Assert.False(state.ShowApplicationTitle);
    }

    [Fact]
    public void Resolve_HidesOnlyVisualTitleBeforeTrailingActionsCanBeClipped()
    {
        Assert.False(AdaptiveShellLayout.Resolve(839.99).ShowApplicationTitle);
        Assert.True(AdaptiveShellLayout.Resolve(840).ShowApplicationTitle);
    }
}
