using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Services;

public sealed record ScanCompletionNotice(
    string Title,
    string Message,
    bool IsPartial = false);

public interface IScanCompletionNotifier
{
    void Notify(ScanCompletionNotice notice);
}

internal interface IWindowsAppNotificationSink
{
    bool TryShow(string title, string message);
}

internal interface ITaskbarAttentionService
{
    void RequestAttention();
}

internal sealed class WindowsScanCompletionNotifier(
    IWindowsAppNotificationSink appNotificationSink,
    ITaskbarAttentionService taskbarAttentionService,
    ILogger<WindowsScanCompletionNotifier> logger) : IScanCompletionNotifier
{
    public void Notify(ScanCompletionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            // Windows App SDK notifications are supported from Windows 10 1809.
            // Keep the gate even though the current binary targets that baseline,
            // so a future legacy build fails closed rather than probing the API.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                _ = appNotificationSink.TryShow(notice.Title, notice.Message);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not show the scan completion notification.");
        }

        try
        {
            taskbarAttentionService.RequestAttention();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not request taskbar attention.");
        }
    }
}
