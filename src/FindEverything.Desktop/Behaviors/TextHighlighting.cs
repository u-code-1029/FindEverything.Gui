using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FindEverything.Desktop.Filtering;

namespace FindEverything.Desktop.Behaviors;

public static class TextHighlighting
{
    public static readonly DependencyProperty DisplayTextProperty = DependencyProperty.RegisterAttached(
        "DisplayText",
        typeof(string),
        typeof(TextHighlighting),
        new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty HighlightTextProperty = DependencyProperty.RegisterAttached(
        "HighlightText",
        typeof(string),
        typeof(TextHighlighting),
        new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    private static readonly Brush FallbackMatchBackground = CreateMatchBackground();

    public static void SetDisplayText(DependencyObject element, string? value) =>
        element.SetValue(DisplayTextProperty, value);

    public static string? GetDisplayText(DependencyObject element) =>
        (string?)element.GetValue(DisplayTextProperty);

    public static void SetHighlightText(DependencyObject element, string? value) =>
        element.SetValue(HighlightTextProperty, value);

    public static string? GetHighlightText(DependencyObject element) =>
        (string?)element.GetValue(HighlightTextProperty);

    private static void OnTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is TextBlock textBlock)
        {
            RebuildInlines(textBlock);
        }
    }

    private static void RebuildInlines(TextBlock textBlock)
    {
        var source = GetDisplayText(textBlock) ?? string.Empty;
        var terms = TextFilter.Terms(GetHighlightText(textBlock));
        textBlock.Inlines.Clear();

        if (terms.Count == 0 || source.Length == 0)
        {
            textBlock.Inlines.Add(new Run(source));
            return;
        }

        var matches = new bool[source.Length];
        foreach (var term in terms)
        {
            var searchFrom = 0;
            while (searchFrom < source.Length)
            {
                var matchIndex = source.IndexOf(term, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                Array.Fill(matches, true, matchIndex, term.Length);
                searchFrom = matchIndex + term.Length;
            }
        }

        var position = 0;
        while (position < source.Length)
        {
            var highlighted = matches[position];
            var end = position + 1;
            while (end < source.Length && matches[end] == highlighted)
            {
                end++;
            }

            var run = new Run(source[position..end]);
            if (highlighted)
            {
                run.Background = SystemParameters.HighContrast
                    ? SystemColors.HighlightBrush
                    : textBlock.TryFindResource("SystemFillColorCautionBackgroundBrush") as Brush
                        ?? FallbackMatchBackground;
                run.Foreground = SystemParameters.HighContrast
                    ? SystemColors.HighlightTextBrush
                    : Brushes.Black;
            }

            textBlock.Inlines.Add(run);
            position = end;
        }
    }

    private static Brush CreateMatchBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 235, 59));
        brush.Freeze();
        return brush;
    }
}
