using System.Windows;
using System.Windows.Automation;

namespace FindEverything.Desktop.Localization;

/// <summary>
/// Legacy, explicit single-object localization. Owned views use resource keys;
/// this opt-in helper never walks descendants or enumerates an ItemsSource.
/// </summary>
public static class LocalizationScope
{
    private const string SourceKeyPrefix = "Loc.Source.";

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(LocalizationScope),
            new FrameworkPropertyMetadata(
                false,
                OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is true) TranslateObject(dependencyObject);
    }

    internal static void TranslateObject(DependencyObject? target)
    {
        if (target is null)
        {
            return;
        }

        var values = target.GetLocalValueEnumerator();
        var replacements = new List<(DependencyProperty Property, string Value)>();
        while (values.MoveNext())
        {
            var entry = values.Current;
            if (entry.Value is not string source
                || string.IsNullOrWhiteSpace(source)
                || !IsLocalizable(entry.Property))
            {
                continue;
            }

            var translated = TryGetTranslation(source);
            if (!string.Equals(source, translated, StringComparison.Ordinal))
            {
                replacements.Add((entry.Property, translated));
            }
        }

        foreach (var replacement in replacements)
        {
            target.SetValue(replacement.Property, replacement.Value);
        }
    }

    internal static bool IsLocalizable(DependencyProperty? property)
    {
        if (property is null)
        {
            return false;
        }

        if (property == AutomationProperties.NameProperty)
        {
            return true;
        }

        if (property == FrameworkElement.NameProperty)
        {
            return false;
        }

        return property.Name is "Text"
            or "Content"
            or "Header"
            or "Title"
            or "Message"
            or "PlaceholderText"
            or "ToolTip"
            or "HelpText"
            or "ItemStatus"
            or "OnContent"
            or "OffContent";
    }

    private static string TryGetTranslation(string source) =>
        System.Windows.Application.Current?.TryFindResource(SourceKeyPrefix + source) as string
        ?? source;
}
