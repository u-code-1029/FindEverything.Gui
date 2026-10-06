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

    private static readonly Brush MatchBackground = CreateMatchBackground();

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
        var term = TextFilter.Normalize(GetHighlightText(textBlock));
        textBlock.Inlines.Clear();

        if (term.Length == 0)
        {
            textBlock.Inlines.Add(new Run(source));
            return;
        }

        var position = 0;
        while (position < source.Length)
        {
            var matchIndex = source.IndexOf(term, position, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                textBlock.Inlines.Add(new Run(source[position..]));
                break;
            }

            if (matchIndex > position)
            {
                textBlock.Inlines.Add(new Run(source[position..matchIndex]));
            }

            var matchingRun = new Run(source.Substring(matchIndex, term.Length))
            {
                Background = SystemParameters.HighContrast
                    ? SystemColors.HighlightBrush
                    : MatchBackground,
                Foreground = SystemParameters.HighContrast
                    ? SystemColors.HighlightTextBrush
                    : Brushes.Black,
            };
            textBlock.Inlines.Add(matchingRun);
            position = matchIndex + term.Length;
        }

        if (source.Length == 0)
        {
            textBlock.Inlines.Add(new Run());
        }
    }

    private static Brush CreateMatchBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 235, 59));
        brush.Freeze();
        return brush;
    }
}
