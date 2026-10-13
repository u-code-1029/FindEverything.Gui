using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
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
    private readonly Dictionary<FrameworkElement, ComboProbe> _probes = [];

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
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var stable = 0;
        var previous = new Point(double.NaN, double.NaN);
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            element.UpdateLayout();
            Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0);
            var center = element.PointToScreen(new Point(element.ActualWidth * xFraction, element.ActualHeight / 2));
            Assert.True(SetCursorPos((int)Math.Round(center.X), (int)Math.Round(center.Y)));
            Pump();
            Mouse.Synchronize();
            var current = element.PointToScreen(new Point(element.ActualWidth * xFraction, element.ActualHeight / 2));
            var atPoint = WindowFromPoint(new NativePoint { X = (int)Math.Round(current.X), Y = (int)Math.Round(current.Y) });
            var source = PresentationSource.FromVisual(element) as HwndSource;
            if ((current - center).Length < .5 && (current - previous).Length < .5
                && source is not null && atPoint == source.Handle && IsWithin(element, Mouse.DirectlyOver as DependencyObject)) stable++;
            else stable = 0;
            previous = current;
            if (stable < 2) continue;
            var probe = _probes.GetValueOrDefault(element);
            probe?.Record("ready-before-down");
            PressAndRelease(probe);
            return;
        }
        Assert.Fail("The real pointer target did not become stable/hit-testable before clicking: " + Describe(element));
    }

    public void ObserveCombo(ComboBox combo, FrameworkElement body)
    {
        if (_probes.ContainsKey(combo)) return;
        var probe = new ComboProbe(combo, body);
        _probes.Add(combo, probe);
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
        var combo = element as ComboBox;
        var toggle = combo?.Template?.FindName("ToggleButton", combo) as ToggleButton;
        var atCenter = WindowFromPoint(new NativePoint { X = (int)Math.Round(center.X), Y = (int)Math.Round(center.Y) });
        string Name(object? candidate) => candidate is FrameworkElement view
            ? $"{view.GetType().Name}#{view.Name}({view.ActualWidth:F1}x{view.ActualHeight:F1}, visible={view.IsVisible},hit={view.IsHitTestVisible},enabled={view.IsEnabled})"
            : candidate?.GetType().Name ?? "none";
        return $"center={center},cursor={pointer.X},{pointer.Y},target={Name(element)}," +
            $"windowActive={_window.IsActive},foreground=0x{GetForegroundWindow():X},targetHwnd=0x{source?.Handle ?? IntPtr.Zero:X}," +
            $"hwndAtCenter=0x{atCenter:X},captureHwnd=0x{GetCapture():X},WpfCaptured={Name(captured)},mouseOver={Name(Mouse.DirectlyOver)}," +
            $"keyboardFocus={Name(Keyboard.FocusedElement)},root={Name(root)},rootHit={Name(hit)},childHit={Name(childHit)}," +
            $"popupOpen={popup?.IsOpen},popupLoaded={popup?.IsLoaded},popupChild={Name(popup?.Child)}," +
            $"toggle={Name(toggle)},togglePressed={toggle?.IsPressed},toggleChecked={toggle?.IsChecked},toggleCaptured={toggle?.IsMouseCaptured}," +
            $"leftButton={Mouse.LeftButton},asyncLeft={(GetAsyncKeyState(1) & 0x8000) != 0},dropDown={combo?.IsDropDownOpen},editable={combo?.IsEditable}," +
            $"probe={_probes.GetValueOrDefault(element)?.Describe() ?? "none"}";
    }

    public void Click(Point screenPoint)
    {
        Assert.True(SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
        Pump();
        PressAndRelease();
    }

    private void PressAndRelease(ComboProbe? probe = null)
    {
        Send(MouseInput(0x0002));
        try { Pump(); probe?.Record("after-down-before-up"); }
        finally { Send(MouseInput(0x0004)); }
        Pump();
        probe?.Record("after-up");
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

    private static bool IsWithin(DependencyObject ancestor, DependencyObject? candidate)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(ancestor, candidate)) return true;
            candidate = candidate is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(candidate) : LogicalTreeHelper.GetParent(candidate);
        }
        return false;
    }

    private sealed class ComboProbe
    {
        private readonly ComboBox _combo;
        private readonly ToggleButton _toggle;
        private readonly List<string> _timeline = [];
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _bodyDown, _comboDown, _toggleDown, _clicks, _opened, _closed;

        public ComboProbe(ComboBox combo, FrameworkElement body)
        {
            _combo = combo;
            combo.ApplyTemplate();
            _toggle = Assert.IsType<ToggleButton>(combo.Template.FindName("ToggleButton", combo));
            body.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
            { _bodyDown++; Record("body-preview-down", e); }), true);
            combo.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
            { _comboDown++; Record("combo-preview-down", e); }), true);
            _toggle.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
            { _toggleDown++; Record("toggle-preview-down", e); }), true);
            _toggle.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, e) => Record("toggle-bubble-down", e)), true);
            _toggle.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) => Record("toggle-bubble-up", e)), true);
            _toggle.Click += (_, _) => { _clicks++; Record("toggle-click"); };
            _toggle.Checked += (_, _) => Record("toggle-checked");
            _toggle.Unchecked += (_, _) => Record("toggle-unchecked");
            combo.DropDownOpened += (_, _) => { _opened++; Record("dropdown-opened"); };
            combo.DropDownClosed += (_, _) => { _closed++; Record("dropdown-closed"); };
        }

        public void Record(string stage, MouseButtonEventArgs? args = null)
        {
            if (_timeline.Count == 32) _timeline.RemoveAt(0);
            _timeline.Add($"{_clock.ElapsedMilliseconds}ms:{stage}(source={args?.OriginalSource?.GetType().Name},handled={args?.Handled}," +
                $"clickCount={args?.ClickCount},eventLeft={args?.LeftButton},deviceLeft={Mouse.LeftButton},asyncLeft={(GetAsyncKeyState(1) & 0x8000) != 0}," +
                $"pressed={_toggle.IsPressed},checked={_toggle.IsChecked},open={_combo.IsDropDownOpen},capture={Mouse.Captured?.GetType().Name},over={Mouse.DirectlyOver?.GetType().Name})");
        }
        public string Describe() => $"bodyDown={_bodyDown},comboDown={_comboDown},toggleDown={_toggleDown},click={_clicks},opened={_opened},closed={_closed};" +
            string.Join(" -> ", _timeline);
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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
