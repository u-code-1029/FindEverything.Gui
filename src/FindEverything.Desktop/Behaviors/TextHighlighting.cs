using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<TextBlock, RenderState> RenderStates = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> TermCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CachedQueries = new();
    private static readonly object CacheGate = new();
    private const int MaximumCachedQueries = 64;

    private sealed class RenderState
    {
        public string? Source;
        public string? Query;
        public bool HighContrast;
        public bool HasFormattedInlines;
    }

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
        var query = TextFilter.Normalize(GetHighlightText(textBlock));
        var state = RenderStates.GetValue(textBlock, static _ => new RenderState());
        var highContrast = SystemParameters.HighContrast;
        if (state.Source == source && state.Query == query && state.HighContrast == highContrast) return;
        state.Source = source;
        state.Query = query;
        state.HighContrast = highContrast;
        var terms = GetTerms(query);

        if (terms.Count == 0 || source.Length == 0)
        {
            SetPlainText(textBlock, state, source);
            return;
        }

        // Store only actual match spans rather than allocating a character-sized
        // bool array for every visible cell, including cells with no matches.
        List<(int Start, int End)>? matches = null;
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

                (matches ??= []).Add((matchIndex, matchIndex + term.Length));
                searchFrom = matchIndex + term.Length;
            }
        }

        if (matches is null)
        {
            SetPlainText(textBlock, state, source);
            return;
        }

        matches.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        textBlock.Inlines.Clear();
        state.HasFormattedInlines = true;
        var position = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Start;
            var end = matches[index].End;
            while (index + 1 < matches.Count && matches[index + 1].Start <= end)
                end = Math.Max(end, matches[++index].End);

            if (start > position) textBlock.Inlines.Add(new Run(source[position..start]));
            var run = new Run(source[start..end]);
            if (highContrast)
            {
                run.Background = SystemColors.HighlightBrush;
                run.Foreground = SystemColors.HighlightTextBrush;
            }
            else
            {
                if (textBlock.TryFindResource("SystemFillColorCautionBackgroundBrush") is Brush)
                    run.SetResourceReference(TextElement.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
                else run.Background = FallbackMatchBackground;
                run.Foreground = Brushes.Black;
            }

            textBlock.Inlines.Add(run);
            position = end;
        }
        if (position < source.Length) textBlock.Inlines.Add(new Run(source[position..]));
    }

    private static void SetPlainText(TextBlock textBlock, RenderState state, string source)
    {
        // Accessing Inlines alone promotes TextBlock to a TextContainer-backed
        // rich-text element. Most result cells have no highlight; let them keep
        // WPF's much cheaper simple-string layout path through row recycling.
        if (state.HasFormattedInlines)
        {
            textBlock.Inlines.Clear();
            state.HasFormattedInlines = false;
        }
        textBlock.Text = source;
    }

    private static IReadOnlyList<string> GetTerms(string query)
    {
        if (query.Length == 0) return [];
        lock (CacheGate)
        {
            if (TermCache.TryGetValue(query, out var terms)) return terms;
            terms = TextFilter.Terms(query);
            TermCache.Add(query, terms);
            CachedQueries.Enqueue(query);
            while (CachedQueries.Count > MaximumCachedQueries) TermCache.Remove(CachedQueries.Dequeue());
            return terms;
        }
    }

    private static Brush CreateMatchBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 235, 59));
        brush.Freeze();
        return brush;
    }
}
