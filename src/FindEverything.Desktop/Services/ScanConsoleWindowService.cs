using FindEverything.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FindEverything.Desktop.Services;

public interface IScanConsoleWindowService
{
    void Show();
}

public sealed class ScanConsoleWindowService(IServiceProvider serviceProvider) :
    IScanConsoleWindowService
{
    private ScanConsoleWindow? _window;

    public void Show()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        if (!application.Dispatcher.CheckAccess())
        {
            _ = application.Dispatcher.InvokeAsync(Show);
            return;
        }

        if (_window is { } existing)
        {
            if (existing.WindowState == System.Windows.WindowState.Minimized)
            {
                existing.WindowState = System.Windows.WindowState.Normal;
            }

            _ = existing.Activate();
            existing.Focus();
            return;
        }

        var window = serviceProvider.GetRequiredService<ScanConsoleWindow>();
        _window = window;
        if (application.MainWindow is { IsLoaded: true } mainWindow
            && !ReferenceEquals(mainWindow, window))
        {
            window.Owner = mainWindow;
        }

        window.Closed += OnWindowClosed;
        window.Show();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not ScanConsoleWindow window)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }
    }
}
