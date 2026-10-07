using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
using Microsoft.Windows.ApplicationModel.DynamicDependency;
#endif

namespace FindEverything.Desktop.Services;

internal sealed class WindowsAppNotificationService(
    ILogger<WindowsAppNotificationService> logger) : IWindowsAppNotificationSink, IHostedService
{
    private readonly object _gate = new();
    private AppNotificationManager? _manager;
    private bool _registered;
#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
    private bool _bootstrapInitialized;
#endif

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return Task.CompletedTask;
        }

        AppNotificationManager? manager = null;
        try
        {
#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
            if (!Bootstrap.TryInitialize(
                    Microsoft.WindowsAppSDK.Release.MajorMinor,
                    Microsoft.WindowsAppSDK.Release.VersionTag,
                    new PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
                    Bootstrap.InitializeOptions.None,
                    out var bootstrapError))
            {
                logger.LogInformation(
                    "Windows App Runtime is not available (HRESULT 0x{Error:X8}).",
                    bootstrapError);
                return Task.CompletedTask;
            }

            _bootstrapInitialized = true;
#endif
            if (!AppNotificationManager.IsSupported())
            {
                logger.LogInformation("Windows app notifications are not supported on this device.");
                return Task.CompletedTask;
            }

            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            var registered = false;
            try
            {
                manager.Register();
                registered = true;
            }
            catch (Exception exception)
            {
                // Windows App SDK 2.5.1 can fail activation registration for an
                // unpackaged self-contained app when its Insights resource DLL
                // is absent. Toast display still works, so keep the manager and
                // degrade only click activation. TryShow remains independently
                // guarded in case a different registration failure also blocks it.
                manager.NotificationInvoked -= OnNotificationInvoked;
                logger.LogInformation(
                    exception,
                    "Notification activation is unavailable; display-only notifications will still be attempted.");
            }

            lock (_gate)
            {
                _manager = manager;
                _registered = registered;
            }
        }
        catch (Exception exception)
        {
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
            }

            // Elevated processes and devices without the required notification
            // capability are expected to land here. Notification support is an
            // optional enhancement and must never prevent the app from starting.
            logger.LogInformation(exception, "Windows app notifications are unavailable.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AppNotificationManager? manager;
        bool registered;
        lock (_gate)
        {
            manager = _manager;
            registered = _registered;
            _manager = null;
            _registered = false;
        }

        if (manager is not null)
        {
            manager.NotificationInvoked -= OnNotificationInvoked;
            if (registered)
            {
                try
                {
                    manager.Unregister();
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Could not unregister Windows app notifications.");
                }
            }
        }

#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
        if (_bootstrapInitialized)
        {
            try
            {
                Bootstrap.Shutdown();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not shut down the Windows App Runtime bootstrapper.");
            }
            finally
            {
                _bootstrapInitialized = false;
            }
        }
#endif

        return Task.CompletedTask;
    }

    public bool TryShow(string title, string message)
    {
        AppNotificationManager? manager;
        lock (_gate)
        {
            manager = _manager;
        }

        if (manager is null)
        {
            return false;
        }

        try
        {
            if (manager.Setting != AppNotificationSetting.Enabled)
            {
                return false;
            }

            var notification = new AppNotificationBuilder()
                .AddArgument("action", "show-scan-results")
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            manager.Show(notification);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not publish a Windows app notification.");
            return false;
        }
    }

    private static void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        var application = System.Windows.Application.Current;
        if (application is null
            || application.Dispatcher.HasShutdownStarted
            || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = application.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () =>
            {
                var window = application.MainWindow;
                if (window is null)
                {
                    return;
                }

                if (!window.IsVisible)
                {
                    window.Show();
                }

                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                _ = window.Activate();
            });
    }
}
