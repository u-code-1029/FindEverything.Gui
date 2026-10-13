using System.ComponentModel.DataAnnotations;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class CatalogDensityMetricsTests
{
    [Theory]
    [InlineData(CatalogRowDensity.Narrow, 36, 2)]
    [InlineData(CatalogRowDensity.Normal, 44, 4)]
    [InlineData(CatalogRowDensity.Wide, 56, 8)]
    public void Density_retains_editor_and_checkbox_space(CatalogRowDensity density, double height, double gap)
    {
        var metrics = CatalogDensityMetrics.Resolve(density);
        Assert.Equal(height, metrics.RowHeight);
        Assert.Equal(gap, metrics.RowMargin.Top + metrics.RowMargin.Bottom);
        Assert.True(32 + metrics.CellPadding.Top + metrics.CellPadding.Bottom
            + metrics.EditorMargin.Top + metrics.EditorMargin.Bottom <= height);
        Assert.True(24 + metrics.CellPadding.Top + metrics.CellPadding.Bottom <= height);
        Assert.Equal(12, metrics.CellPadding.Left);
        Assert.Equal(12, metrics.CellPadding.Right);
    }

    [Fact]
    public void Unknown_density_is_rejected_before_it_can_be_applied()
    {
        var options = new AppearanceOptions { CatalogRowDensity = (CatalogRowDensity)77 };
        var errors = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new(options), errors, true));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(AppearanceOptions.CatalogRowDensity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogDensityMetrics.Resolve(options.CatalogRowDensity));
    }
}
