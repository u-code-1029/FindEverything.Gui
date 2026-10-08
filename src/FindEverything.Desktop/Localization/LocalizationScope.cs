using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace FindEverything.Desktop.Localization;

/// <summary>
/// Localizes literal control text once when a cached page is created. The app
/// intentionally restarts for a culture change, so bindings and long-running
/// operation state never contain a mixture of two languages.
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
                FrameworkPropertyMetadataOptions.Inherits,
                OnIsEnabledChanged));

    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsHooked",
            typeof(bool),
            typeof(LocalizationScope));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is not true || dependencyObject is not FrameworkElement root)
        {
            return;
        }

        if (!(bool)root.GetValue(IsHookedProperty))
        {
            root.SetValue(IsHookedProperty, true);
            root.AddHandler(
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnDescendantLoaded),
                handledEventsToo: true);
        }

        TranslateTree(root);
    }

    private static void OnDescendantLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is DependencyObject source)
        {
            TranslateObject(source);
        }
    }

    private static void TranslateTree(DependencyObject root) =>
        TranslateTree(root, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));

    private static void TranslateTree(
        DependencyObject root,
        ISet<DependencyObject> visited)
    {
        if (!visited.Add(root))
        {
            return;
        }

        TranslateObject(root);

        if (root is DataGrid dataGrid)
        {
            foreach (var column in dataGrid.Columns)
            {
                TranslateObject(column);
            }
        }

        if (root is FrameworkElement element && element.ContextMenu is { } contextMenu)
        {
            TranslateTree(contextMenu, visited);
        }

        if (root is ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
            {
                if (item is DependencyObject dependencyItem)
                {
                    TranslateTree(dependencyItem, visited);
                }
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            TranslateTree(child, visited);
        }

        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                TranslateTree(child, visited);
            }
        }
    }

    private static void TranslateObject(DependencyObject target)
    {
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

    internal static bool IsLocalizable(DependencyProperty property)
    {
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
