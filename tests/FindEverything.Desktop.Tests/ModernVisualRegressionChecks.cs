using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>
/// The visual experiment may change surface styling, not keyboard access or
/// the rendering contracts established by the responsiveness work. Run on the
/// composition smoke test's existing WPF application dispatcher.
/// </summary>
internal static class ModernVisualRegressionChecks
{
    internal static void VerifySharedStyles()
    {
        var resources = System.Windows.Application.Current.Resources;
        Assert.Equal(new CornerRadius(12), resources["SurfaceCornerRadius"]);
        Assert.Equal(new Thickness(16), resources["CardPadding"]);

        foreach (var (key, padding) in new[]
        {
            ("FluentCardStyle", new Thickness(16)),
            ("WorkspaceToolbarCardStyle", new Thickness(12, 8, 12, 8)),
            ("WorkspaceResultCardStyle", new Thickness(4, 0, 4, 0)),
            ("WorkspaceActionCardStyle", new Thickness(12, 8, 12, 8)),
        })
        {
            var card = new SectionCard { Style = Assert.IsType<Style>(resources[key]) };
            Assert.Equal(new CornerRadius(12), card.CornerRadius);
            Assert.Equal(padding, card.Padding);
            Assert.False(card.Focusable);
            Assert.False(card.IsTabStop);
            Assert.Null(card.Effect);
            Assert.Equal(HorizontalAlignment.Stretch, card.HorizontalContentAlignment);
        }

        var search = new Ui.TextBox { Style = Assert.IsType<Style>(resources["FluentSearchBoxStyle"]) };
        Assert.Equal(44, search.MinHeight);
        Assert.Equal(VerticalAlignment.Center, search.VerticalContentAlignment);
        Assert.True(search.Focusable);
        Assert.True(search.IsTabStop);
        Assert.Null(search.Effect);

        var selector = new ComboBox { Style = Assert.IsType<Style>(resources["ContentAwareComboBoxStyle"]) };
        Assert.Equal(40, selector.MinHeight);
        Assert.True(double.IsNaN(selector.Height), "Font metrics, not a fixed height, own selector growth.");
        Assert.Equal(new Thickness(12, 8, 12, 8), selector.Padding);
        Assert.Equal(VerticalAlignment.Center, selector.VerticalContentAlignment);

        var action = new Ui.Button { Style = Assert.IsType<Style>(resources["CompactActionButtonStyle"]) };
        Assert.Equal(32, action.MinHeight);
        Assert.True(double.IsNaN(action.Height));
        Assert.Equal(new Thickness(10, 4, 10, 4), action.Padding);
        var icon = new Ui.Button { Style = Assert.IsType<Style>(resources["CompactIconButtonStyle"]) };
        Assert.Equal(32, icon.Width);
        Assert.Equal(32, icon.Height);
        Assert.Equal(new Thickness(4), icon.Padding);
        Assert.True(icon.Height - icon.Padding.Top - icon.Padding.Bottom
            - icon.BorderThickness.Top - icon.BorderThickness.Bottom >= 20,
            "A 20 DIP Fluent icon must fit inside its 32 DIP hit target.");
    }

    internal static void VerifyResultInputs(FilesPage files, CatalogPage catalog)
    {
        foreach (var page in new Page[] { files, catalog })
        {
            var grid = Assert.IsAssignableFrom<DataGrid>(page.FindName("ResultsGrid"));
            Assert.True(grid.EnableRowVirtualization);
            Assert.True(grid.EnableColumnVirtualization);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(grid));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
            Assert.Null(grid.Effect);

            var search = Assert.IsType<Ui.TextBox>(page.FindName("SearchBox"));
            var binding = Assert.IsType<Binding>(BindingOperations.GetBinding(search, TextBox.TextProperty));
            Assert.Equal(UpdateSourceTrigger.PropertyChanged, binding.UpdateSourceTrigger);
            // The view models own the 700 ms debounce. A second XAML delay
            // would make input feel slow again even though the surface is new.
            Assert.Equal(0, binding.Delay);
            Assert.True(search.Focusable);
            Assert.True(search.IsTabStop);
            Assert.Null(search.Effect);
        }
    }
}
