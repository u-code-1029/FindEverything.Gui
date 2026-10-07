using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FindEverything.Desktop.Services;

internal sealed partial class WindowsTaskbarAttentionService : ITaskbarAttentionService
{
    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilForeground = 0x0000000C;

    public void RequestAttention()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var application = System.Windows.Application.Current;
        if (application is null
            || application.Dispatcher.HasShutdownStarted
            || application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            FlashMainWindow(application);
            return;
        }

        _ = application.Dispatcher.BeginInvoke(() => FlashMainWindow(application));
    }

    private static void FlashMainWindow(System.Windows.Application application)
    {
        var window = application.MainWindow;
        if (window is null || !window.IsVisible || window.IsActive)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = checked((uint)Marshal.SizeOf<FlashWindowInfo>()),
            WindowHandle = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue,
            TimeoutMilliseconds = 0,
        };
        _ = FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint TimeoutMilliseconds;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo flashInfo);
}
