using System.Windows;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>Marks page-local bottom actions which a floating notice must not cover.</summary>
public static class FloatingNotificationChrome
{
    public static readonly DependencyProperty IsBottomChromeProperty = DependencyProperty.RegisterAttached(
        "IsBottomChrome", typeof(bool), typeof(FloatingNotificationChrome), new(false, OnChanged));

    public static void SetIsBottomChrome(DependencyObject element, bool value) => element.SetValue(IsBottomChromeProperty, value);
    public static bool GetIsBottomChrome(DependencyObject element) => (bool)element.GetValue(IsBottomChromeProperty);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement chrome) return;
        if ((bool)args.NewValue)
        {
            chrome.Loaded += OnLoaded;
            chrome.Unloaded += OnUnloaded;
            chrome.SizeChanged += OnLayoutChanged;
            chrome.IsVisibleChanged += OnVisibilityChanged;
            if (chrome.IsLoaded) Register(chrome);
        }
        else
        {
            Unregister(chrome);
            chrome.Loaded -= OnLoaded;
            chrome.Unloaded -= OnUnloaded;
            chrome.SizeChanged -= OnLayoutChanged;
            chrome.IsVisibleChanged -= OnVisibilityChanged;
        }
    }

    private static void Register(FrameworkElement element) => (Window.GetWindow(element) as MainWindow)?.RegisterNotificationChrome(element);
    private static void Unregister(FrameworkElement element) => (Window.GetWindow(element) as MainWindow)?.UnregisterNotificationChrome(element);
    private static void OnLoaded(object sender, RoutedEventArgs args) => Register((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs args) => Unregister((FrameworkElement)sender);
    private static void OnLayoutChanged(object sender, SizeChangedEventArgs args) => Register((FrameworkElement)sender);
    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Register((FrameworkElement)sender);
}
