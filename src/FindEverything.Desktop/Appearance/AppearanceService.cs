using FindEverything.Application.Options;
using Microsoft.Extensions.Options;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Appearance;

public interface IAppearanceService
{
    void Attach(FluentWindow window);

    void Apply(FluentWindow window, AppearanceOptions options);

    void ApplyToAttachedWindow(AppearanceOptions options);
}

public sealed class AppearanceService : IAppearanceService, IDisposable
{
    private readonly IDisposable? _subscription;
    private FluentWindow? _watchedWindow;
    private FluentWindow? _attachedWindow;

    public AppearanceService(IOptionsMonitor<AppearanceOptions> monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _subscription = monitor.OnChange(options =>
            ApplyToAttachedWindow(options));
    }

    public void Attach(FluentWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Volatile.Write(ref _attachedWindow, window);
    }

    public void Apply(FluentWindow window, AppearanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);

        if (!window.Dispatcher.CheckAccess())
        {
            _ = window.Dispatcher.InvokeAsync(() => Apply(window, options));
            return;
        }

        var backdrop = options.Backdrop == BackdropPreference.Auto
            && WindowBackdrop.IsSupported(WindowBackdropType.Auto)
                ? WindowBackdropType.Auto
                : WindowBackdropType.None;
        // WPF-UI rebuilds WindowChrome when this dependency property changes.
        // Re-applying the same value is unnecessary and can make a headless WPF
        // host detach the same Freezable inheritance context twice.
        if (window.WindowBackdropType != backdrop)
        {
            window.SetCurrentValue(FluentWindow.WindowBackdropTypeProperty, backdrop);
        }

        StopWatchingIfNecessary(window);
        switch (options.Theme)
        {
            case ThemePreference.System:
                SystemThemeWatcher.Watch(window, backdrop);
                _watchedWindow = window;
                break;
            case ThemePreference.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdrop);
                break;
            case ThemePreference.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, backdrop);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Theme,
                    "지원되지 않는 테마 설정입니다.");
        }
    }

    public void ApplyToAttachedWindow(AppearanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var window = Volatile.Read(ref _attachedWindow);
        if (window is not null)
        {
            Apply(window, options);
        }
    }

    private void StopWatchingIfNecessary(FluentWindow window)
    {
        if (!ReferenceEquals(_watchedWindow, window) || !window.IsLoaded)
        {
            return;
        }

        SystemThemeWatcher.UnWatch(window);
        _watchedWindow = null;
    }

    public void Dispose() => _subscription?.Dispose();
}
