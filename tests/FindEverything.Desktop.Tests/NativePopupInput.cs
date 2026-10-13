using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
}
