using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Real HWND input, not RaiseEvent (which cannot reproduce capture).</summary>
internal sealed class NativePopupInput : IDisposable
{
    private readonly Window _window;
    private readonly NativePoint _original;
    private readonly bool _restore;

    public NativePopupInput(Window window)
    {
        _window = window;
        _restore = GetCursorPos(out _original);
        Assert.True(window.Activate(), "The native popup input window must be active.");
        Pump();
        Assert.True(window.IsActive);
    }

    public void Click(FrameworkElement element, double xFraction = .5)
    {
        element.UpdateLayout();
        Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0);
        Click(element.PointToScreen(new Point(element.ActualWidth * xFraction, element.ActualHeight / 2)));
    }

    public string Describe(FrameworkElement element, Popup? popup = null)
    {
        var center = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        _ = GetCursorPos(out var pointer);
        var source = PresentationSource.FromVisual(element) as HwndSource;
        var captured = Mouse.Captured as DependencyObject;
        var root = source?.RootVisual as UIElement;
        var hit = root?.InputHitTest(root.PointFromScreen(center));
        var child = popup?.Child as UIElement;
        var childHit = child?.InputHitTest(child.PointFromScreen(center));
        var toggle = element is System.Windows.Controls.ComboBox combo ? combo.Template?.FindName("ToggleButton", combo) : null;
        var atCenter = WindowFromPoint(new NativePoint { X = (int)Math.Round(center.X), Y = (int)Math.Round(center.Y) });
        string Name(object? candidate) => candidate is FrameworkElement view
            ? $"{view.GetType().Name}#{view.Name}({view.ActualWidth:F1}x{view.ActualHeight:F1}, visible={view.IsVisible},hit={view.IsHitTestVisible},enabled={view.IsEnabled})"
            : candidate?.GetType().Name ?? "none";
        return $"center={center},cursor={pointer.X},{pointer.Y},target={Name(element)}," +
            $"windowActive={_window.IsActive},foreground=0x{GetForegroundWindow():X},targetHwnd=0x{source?.Handle ?? IntPtr.Zero:X}," +
            $"hwndAtCenter=0x{atCenter:X},captureHwnd=0x{GetCapture():X},WpfCaptured={Name(captured)},mouseOver={Name(Mouse.DirectlyOver)}," +
            $"keyboardFocus={Name(Keyboard.FocusedElement)},root={Name(root)},rootHit={Name(hit)},childHit={Name(childHit)}," +
            $"popupOpen={popup?.IsOpen},popupLoaded={popup?.IsLoaded},popupChild={Name(popup?.Child)}," +
            $"toggle={Name(toggle)}";
    }

    public void Click(Point screenPoint)
    {
        Assert.True(SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
        Pump();
        Send(MouseInput(0x0002));
        try { Pump(); }
        finally { Send(MouseInput(0x0004)); }
        Pump();
    }

    public void Key(ushort key)
    {
        Send(KeyboardInput(key, 0, 0), KeyboardInput(key, 0, 0x0002));
        Pump();
    }

    public void Type(string text)
    {
        foreach (var character in text)
            Send(KeyboardInput(0, character, 0x0004), KeyboardInput(0, character, 0x0006));
        Pump();
    }

    private static void Send(params NativeInput[] input) => Assert.Equal((uint)input.Length,
        SendInput((uint)input.Length, input, Marshal.SizeOf<NativeInput>()));

    private static NativeInput MouseInput(uint flags) => new()
    {
        Type = 0,
        Data = new() { Mouse = new() { X = 0, Y = 0, Data = 0, Flags = flags, Time = 0, Extra = UIntPtr.Zero } },
    };
    private static NativeInput KeyboardInput(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = 1,
        Data = new() { Keyboard = new() { VirtualKey = virtualKey, Scan = scan, Flags = flags, Time = 0, Extra = UIntPtr.Zero } },
    };

    private void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        _window.UpdateLayout();
    }

    public void Dispose()
    {
        if (_restore) _ = SetCursorPos(_original.X, _original.Y);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse
    {
        public int X; public int Y; public uint Data; public uint Flags; public uint Time; public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard
    {
        public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputData
    {
        [FieldOffset(0)] public NativeMouse Mouse;
        [FieldOffset(0)] public NativeKeyboard Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputData Data; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int inputSize);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}
