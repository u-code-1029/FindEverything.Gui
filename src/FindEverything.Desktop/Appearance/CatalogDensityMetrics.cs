using System.Windows;
using FindEverything.Application.Options;

namespace FindEverything.Desktop.Appearance;

public sealed record CatalogDensityMetrics(double RowHeight, Thickness RowMargin,
    Thickness CellPadding, Thickness EditorMargin)
{
    public static CatalogDensityMetrics Resolve(CatalogRowDensity density) => density switch
    {
        CatalogRowDensity.Narrow => new(36, new(0, 1, 0, 1), new(12, 2, 12, 2), new(4, 0, 4, 0)),
        CatalogRowDensity.Normal => new(44, new(0, 2, 0, 2), new(12, 4, 12, 4), new(4, 2, 4, 2)),
        CatalogRowDensity.Wide => new(56, new(0, 4, 0, 4), new(12, 8, 12, 8), new(4, 4, 4, 4)),
        _ => throw new ArgumentOutOfRangeException(nameof(density)),
    };

    internal static void Apply(ResourceDictionary resources, CatalogRowDensity density)
    {
        var metrics = Resolve(density);
        resources["CatalogRowHeight"] = metrics.RowHeight;
        resources["CatalogRowMargin"] = metrics.RowMargin;
        resources["CatalogCellPadding"] = metrics.CellPadding;
        resources["CatalogEditorMargin"] = metrics.EditorMargin;
    }
}
