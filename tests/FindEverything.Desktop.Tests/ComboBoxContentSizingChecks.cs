using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.Behaviors;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Run on the composition smoke application's dispatcher after resources load.</summary>
internal static class ComboBoxContentSizingChecks
{
    internal static void Verify()
    {
        var choices = Enumerable.Range(0, 1000).Select(index => new Choice(index == 0 ? "Short" : "Not selected")).ToArray();
        var combo = new ComboBox
        {
            Style = (Style)System.Windows.Application.Current.FindResource("ContentAwareComboBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            DisplayMemberPath = nameof(Choice.Label),
            ItemsSource = choices,
            SelectedIndex = 0,
            ToolTip = "Choose the profile used to parse folders."
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.Children.Add(combo);
        var neighbor = new Button { Content = "Keep this action visible", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(neighbor, 1);
        grid.Children.Add(neighbor);
        var host = new Window { Content = grid, Width = 760, Height = 240, ShowInTaskbar = false };
        host.Show();
        try
        {
            Pump(host);
            var originalTemplate = combo.Template;
            var originalTip = combo.ToolTip;
            var shortWidth = combo.ActualWidth;
            Assert.Equal("Short", ComboBoxContentSizing.GetFullSelectedText(combo));
            choices[0].Label = "Production archive";
            Pump(host);
            Assert.Equal("Production archive", ComboBoxContentSizing.GetFullSelectedText(combo));
            Assert.True(combo.ActualWidth > shortWidth + 20, "Selection content should reserve readable space.");
            var normalWidth = combo.ActualWidth;
            combo.FontSize = 22;
            Pump(host);
            Assert.True(combo.ActualWidth > normalWidth, "Font changes must refresh the selected label's measured width.");
            Assert.Same(originalTemplate, combo.Template);
            Assert.All(choices.Skip(1), item => Assert.Equal(0, item.ReadCount));

            choices[0].Label = new string('W', 2000);
            host.Width = 480;
            Pump(host);
            Assert.InRange(combo.ActualWidth, 1, grid.ColumnDefinitions[0].ActualWidth + 1);
            Assert.True(combo.TranslatePoint(new Point(combo.ActualWidth, 0), grid).X <= neighbor.TranslatePoint(new Point(), grid).X);
            Assert.Equal(choices[0].Label, ComboBoxContentSizing.GetFullSelectedText(combo));
            Assert.Same(originalTip, combo.ToolTip);
            Assert.True(combo.IsHitTestVisible);
            var tip = Assert.IsType<ToolTip>(ComboBoxContentSizing.ShowFullSelectedText(combo));
            Assert.Same(combo, tip.PlacementTarget);
            Assert.True(tip.IsOpen);
            var tipText = Assert.IsType<TextBlock>(tip.Content);
            Assert.Contains(choices[0].Label, tipText.Text);
            Assert.Contains((string)originalTip, tipText.Text);
            Assert.Equal(TextWrapping.Wrap, tipText.TextWrapping);
            tip.IsOpen = false;

            // Removing a retained page must detach property and ancestor handlers;
            // returning to the page re-evaluates the current selected value once.
            host.Content = null;
            Pump(host);
            var previousText = ComboBoxContentSizing.GetFullSelectedText(combo);
            choices[0].Label = "Updated while hidden";
            combo.FontSize = 14;
            Pump(host);
            Assert.Equal(previousText, ComboBoxContentSizing.GetFullSelectedText(combo));
            host.Content = grid;
            Pump(host);
            Assert.Equal("Updated while hidden", ComboBoxContentSizing.GetFullSelectedText(combo));

            VerifyLocalizedItem(host, combo);
            VerifyCustomTemplate(host, combo);
            ComboBoxContentSizing.SetIsEnabled(combo, false);
            Assert.Equal(0, ComboBoxContentSizing.GetDesiredMinimumWidth(combo));
            Assert.True(double.IsPositiveInfinity(ComboBoxContentSizing.GetAvailableWidth(combo)));
            Assert.Same(originalTip, combo.ToolTip);
            ComboBoxContentSizing.SetIsEnabled(combo, true);
            Pump(host);
            Assert.Equal("Explicit accessible label", ComboBoxContentSizing.GetFullSelectedText(combo));
            VerifyAutoSizedContainers(host, combo.Style);
            VerifySelectedTextGeometry(host, combo.Style);
        }
        finally { host.Close(); }
    }

    private static void VerifySelectedTextGeometry(Window host, Style style)
    {
        var original = host.Content;
        var originalWidth = host.Width;
        var originalHeight = host.Height;
        var choice = new Choice("프로젝트 담당자 김민수");
        var combo = new ComboBox
        {
            Style = style, ItemsSource = new[] { choice, new Choice("Other choice") },
            SelectedItem = choice, DisplayMemberPath = nameof(Choice.Label),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var surface = new Grid { Margin = new Thickness(16) };
        surface.Children.Add(combo);
        host.Content = surface;
        host.Width = 1000;
        host.Height = 320;
        try
        {
            Pump(host);
            var nativeTemplate = combo.Template;
            Assert.NotNull(nativeTemplate);
            Assert.True(combo.Focusable && combo.IsTabStop, "Content sizing must preserve native keyboard/tab accessibility.");
            Assert.True(combo.MinHeight >= 40, "The selected label needs a padded minimum hit target.");
            Assert.True(combo.Padding.Top >= 8 && combo.Padding.Bottom >= 8);
            Assert.Equal(VerticalAlignment.Center, combo.VerticalContentAlignment);

            // LayoutTransform checks logical layout scaling, not a monitor-DPI
            // switch. Actual 100/150/200% per-monitor DPI still needs Windows QA.
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            foreach (var fontSize in new[] { 14d, 18d, 22d })
            foreach (var label in new[] { "프로젝트 담당자 김민수", "Project owner gjpqy" })
            {
                surface.LayoutTransform = new ScaleTransform(scale, scale);
                combo.FontSize = fontSize;
                choice.Label = label;
                Pump(host);
                var context = $"{label}, font {fontSize}, logical scale {scale:P0}";
                AssertSelectedContentFits(combo, requireFullTextWidth: true, context);
                Assert.Same(nativeTemplate, combo.Template);
            }

            // Popup items use a separate native template/presentation source.
            // Check the selected item too, without opening the 1,000-item fixture.
            surface.LayoutTransform = Transform.Identity;
            combo.FontSize = 22;
            combo.IsDropDownOpen = true;
            Pump(host);
            var selectedContainer = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(selectedContainer.IsSelected);
            var selectedText = Descendants(selectedContainer).OfType<TextBlock>()
                .FirstOrDefault(text => text.IsVisible && text.Text == choice.Label);
            Assert.NotNull(selectedText);
            AssertTextFitsVertically(selectedText, selectedContainer, "Native selected dropdown item");
        }
        finally
        {
            combo.IsDropDownOpen = false;
            host.Content = original;
            host.Width = originalWidth;
            host.Height = originalHeight;
            Pump(host);
        }
    }

    internal static void AssertSelectedContentFits(ComboBox combo, bool requireFullTextWidth, string context)
    {
        var presenter = combo.Template?.FindName("ContentSite", combo) as ContentPresenter
            ?? Descendants(combo).OfType<ContentPresenter>()
                .FirstOrDefault(candidate => Equals(candidate.Content, combo.SelectionBoxItem));
        Assert.NotNull(presenter);
        Assert.True(presenter.ActualHeight > 0, $"{context}: selected ContentPresenter must have a usable height.");
        var texts = Descendants(presenter).OfType<TextBlock>()
            .Where(static text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)).ToArray();
        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            AssertTextFitsVertically(text, combo, context);
            if (!requireFullTextWidth) continue; // Long profile labels may intentionally ellipsize horizontally.
            var formatted = MeasureText(text);
            Assert.True(text.ActualWidth + 1 >= formatted.WidthIncludingTrailingWhitespace,
                $"{context}: selected text width {text.ActualWidth} must accommodate {formatted.WidthIncludingTrailingWhitespace}.");
            var bounds = text.TransformToAncestor(presenter).TransformBounds(new Rect(text.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= presenter.ActualWidth + 1,
                $"{context}: selected text must fit the native ContentPresenter horizontally.");
        }
    }

    internal static void AssertTextFitsVertically(TextBlock text, FrameworkElement boundary, string context)
    {
        var formatted = MeasureText(text);
        var ink = formatted.BuildGeometry(new Point()).Bounds;
        Assert.True(text.ActualHeight + 1 >= formatted.Height,
            $"{context}: text height {text.ActualHeight} clips the natural line height {formatted.Height}.");
        Assert.True(ink.IsEmpty || (ink.Top >= -1 && ink.Bottom <= text.ActualHeight + 1),
            $"{context}: native glyph bounds {ink} must fit, including lower-case descenders.");
        DependencyObject? ancestor = text;
        while (ancestor is not null && !ReferenceEquals(ancestor, boundary))
        {
            ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is not FrameworkElement parent || parent.ActualHeight <= 0) continue;
            var bounds = text.TransformToAncestor(parent).TransformBounds(new Rect(text.RenderSize));
            Assert.True(bounds.Top >= -1 && bounds.Bottom <= parent.ActualHeight + 1,
                $"{context}: {text.GetType().Name} vertically exceeds native ancestor {parent.GetType().Name} ({bounds} vs {parent.ActualHeight}).");
        }
        Assert.Same(boundary, ancestor);
    }

    private static FormattedText MeasureText(TextBlock text)
    {
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground ?? Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        if (double.IsFinite(text.LineHeight) && text.LineHeight > 0) formatted.LineHeight = text.LineHeight;
        return formatted;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void VerifyAutoSizedContainers(Window host, Style style)
    {
        var original = host.Content;
        var item = new Choice("Short");
        var combo = new ComboBox { Style = style, ItemsSource = new[] { item }, SelectedItem = item, DisplayMemberPath = nameof(Choice.Label) };
        var panel = new StackPanel { MinWidth = 240, MaxWidth = 480 };
        panel.Children.Add(combo);
        var wrap = new WrapPanel();
        wrap.Children.Add(panel);
        host.Width = 760;
        host.Content = wrap;
        var popup = new Popup { PlacementTarget = host, Placement = PlacementMode.Bottom, AllowsTransparency = true };
        try
        {
            Pump(host);
            var initialWidth = panel.ActualWidth;
            item.Label = new string('W', 80);
            Pump(host);
            Assert.InRange(panel.ActualWidth, initialWidth + 80, 480);

            // A popup measures its child with infinite width. ActualWidth is the
            // previous content size, not an externally allocated width limit.
            wrap.Children.Remove(panel);
            popup.Child = new Wpf.Ui.Controls.DynamicScrollViewer
            {
                Content = panel, MaxHeight = 240,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            item.Label = "Short";
            popup.IsOpen = true;
            Pump(host);
            initialWidth = panel.ActualWidth;
            Assert.InRange(initialWidth, 239, 241);
            item.Label = new string('W', 80);
            Pump(host);
            Assert.InRange(panel.ActualWidth, initialWidth + 80, 480);
            Assert.True(ComboBoxContentSizing.GetAvailableWidth(combo) <= 480);

            // Temporarily supplying a missing-tooltip trigger must retain a live
            // binding and its later help value, including when the behavior stops.
            var helpSource = new TextBlock();
            combo.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(FrameworkElement.Tag)) { Source = helpSource });
            Pump(host);
            Assert.NotNull(BindingOperations.GetBindingExpression(combo, FrameworkElement.ToolTipProperty));
            helpSource.Tag = "Changed help from a binding";
            Pump(host);
            Assert.Equal("Changed help from a binding", combo.ToolTip);
            var tip = Assert.IsType<ToolTip>(ComboBoxContentSizing.ShowFullSelectedText(combo));
            Assert.Contains("Changed help from a binding", Assert.IsType<TextBlock>(tip.Content).Text);
            ComboBoxContentSizing.SetIsEnabled(combo, false);
            Assert.False(tip.IsOpen);
            Assert.Equal("Changed help from a binding", combo.ToolTip);
            Assert.NotNull(BindingOperations.GetBindingExpression(combo, FrameworkElement.ToolTipProperty));
        }
        finally
        {
            popup.IsOpen = false;
            popup.Child = null;
            host.Content = original;
            Pump(host);
        }
    }

    private static void VerifyLocalizedItem(Window host, ComboBox combo)
    {
        combo.ItemsSource = null;
        combo.DisplayMemberPath = string.Empty;
        var item = new ComboBoxItem();
        host.Resources["ComboSizing.Test.Label"] = "보통";
        item.SetResourceReference(ContentControl.ContentProperty, "ComboSizing.Test.Label");
        combo.Items.Add(item);
        combo.SelectedItem = item;
        Pump(host);
        Assert.Equal("보통", ComboBoxContentSizing.GetFullSelectedText(combo));
        host.Resources["ComboSizing.Test.Label"] = "Comfortable spacing";
        Pump(host);
        Assert.Equal("Comfortable spacing", ComboBoxContentSizing.GetFullSelectedText(combo));
    }

    private static void VerifyCustomTemplate(Window host, ComboBox combo)
    {
        combo.Items.Clear();
        combo.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding Label}" />
            </DataTemplate>
            """);
        var value = new Choice("Template display label");
        combo.ItemsSource = new[] { value };
        combo.SelectedItem = value;
        Pump(host);
        Assert.Equal("Template display label", ComboBoxContentSizing.GetFullSelectedText(combo));
        value.Label = "Updated template label";
        Pump(host);
        Assert.Equal("Updated template label", ComboBoxContentSizing.GetFullSelectedText(combo));

        ComboBoxContentSizing.SetSelectedText(combo, "Explicit accessible label");
        Pump(host);
        Assert.Equal("Explicit accessible label", ComboBoxContentSizing.GetFullSelectedText(combo));
    }

    private static void Pump(Window host)
    {
        host.UpdateLayout();
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        host.UpdateLayout();
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    private sealed class Choice(string label) : INotifyPropertyChanged
    {
        private string _label = label;
        public int ReadCount { get; private set; }
        public string Label
        {
            get { ReadCount++; return _label; }
            set { _label = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => "Not the display label";
    }
}
