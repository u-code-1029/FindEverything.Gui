using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs on the existing composition application/STA dispatcher.</summary>
internal static class ReadOnlyCellTemplateChecks
{
    internal static void Verify()
    {
        var value = new CellValue("A long read-only path " + new string('x', 256));
        var cell = new SelectableCatalogText(new Binding(nameof(CellValue.Value)) { Mode = BindingMode.OneWay })
        {
            DataContext = value, Width = 180, VerticalAlignment = VerticalAlignment.Center,
        };
        var other = new Wpf.Ui.Controls.Button { Content = "Focus elsewhere", Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(cell); panel.Children.Add(other);
        var window = new Window { Width = 300, Height = 180, Content = panel, ShowInTaskbar = false };
        var originalPointer = default(NativePoint);
        var hasOriginalPointer = GetCursorPos(out originalPointer);
        var originalPointerError = Marshal.GetLastWin32Error();
        var pointerDown = false;
        var rowToggleRequests = 0;
        panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // This is the same guard used by the catalog's row toggle handlers.
            // An idle, collapsed content host must still hit the native editor.
            if (!ResultGridInput.IsInteractiveOrigin(e.OriginalSource as DependencyObject)) rowToggleRequests++;
        };
        try
        {
            window.Show();
            var activated = window.Activate();
            other.Focus();
            PumpInput(window);
            Assert.True(hasOriginalPointer,
                $"GetCursorPos returned false (Win32={originalPointerError}); {InputDiagnostics(window)}");
            Assert.True(activated && window.IsActive,
                $"The native input test window did not activate (Activate={activated}); {InputDiagnostics(window)}");
            var editor = cell.TextBox;
            Assert.IsType<Wpf.Ui.Controls.TextBox>(editor);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.IsUndoEnabled);
            Assert.True(editor.OverridesDefaultStyle);
            Assert.Null(editor.Style.BasedOn);
            Assert.False(editor.PlaceholderEnabled);
            editor.ApplyTemplate();
            var host = Assert.IsType<ScrollViewer>(editor.Template.FindName("PART_ContentHost", editor));
            var inputSurface = Assert.IsType<Border>(editor.Template.FindName("PART_InputSurface", editor));
            host.ApplyTemplate();
            Assert.IsType<ScrollContentPresenter>(host.Template.FindName("PART_ScrollContentPresenter", host));
            Assert.Empty(Descendants<ButtonBase>(editor));
            Assert.Same(inputSurface, Assert.Single(Descendants<Border>(editor)));
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(inputSurface.Background).Color);
            Assert.Equal(new Thickness(0), inputSurface.BorderThickness);
            Assert.Empty(Descendants<Grid>(editor));
            Assert.Empty(Descendants<ScrollBar>(editor));
            MovePointer(window, other.PointToScreen(new Point(8, other.ActualHeight / 2)));
            PumpInput(window);
            Assert.Equal(Visibility.Collapsed, host.Visibility);
            Assert.Equal(0, host.DesiredSize.Width);
            Assert.Equal(0, host.DesiredSize.Height);
            Assert.Equal(value.Value, editor.Text);
            var binding = Assert.IsType<Binding>(BindingOperations.GetBindingBase(editor,
                System.Windows.Controls.TextBox.TextProperty));
            Assert.Equal(BindingMode.OneWay, binding.Mode);

            // Exercise a real native pointer route, including the first mouse
            // down after idle. This catches a content host that is cheap only
            // because it stopped receiving/selecting native text input.
            MovePointer(window, editor.PointToScreen(new Point(3, editor.ActualHeight / 2)));
            PumpInput(window);
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.True(ResultGridInput.IsInteractiveOrigin(Mouse.DirectlyOver as DependencyObject));
            SendMouse(window, MouseLeftDown);
            pointerDown = true;
            PumpInput(window);
            Assert.True(editor.IsKeyboardFocusWithin,
                $"Native mouse down did not focus the real text editor; {InputDiagnostics(window)}");
            MovePointer(window, editor.PointToScreen(new Point(90, editor.ActualHeight / 2)));
            PumpInput(window);
            SendMouse(window, MouseLeftUp);
            pointerDown = false;
            PumpInput(window);
            Assert.True(editor.SelectionLength > 0,
                $"Native mouse drag did not select text (host={host.Visibility}, viewport={host.ViewportWidth}, " +
                $"editorFocus={editor.IsKeyboardFocusWithin}); {InputDiagnostics(window)}");
            Assert.Equal(value.Value.Substring(editor.SelectionStart, editor.SelectionLength), editor.SelectedText);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, editor));
            string? copiedText = null;
            DataObject.AddCopyingHandler(editor, (_, e) =>
            {
                copiedText = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                // Verify native copy's payload without touching the shared
                // system clipboard or making a locked clipboard look like an
                // editor-selection regression on a headless Windows runner.
                e.CancelCommand();
            });
            ApplicationCommands.Copy.Execute(null, editor);
            Assert.Equal(editor.SelectedText, copiedText);
            Assert.Equal(0, rowToggleRequests);
            Assert.Equal(value.Value, editor.Text);

            editor.Focus();
            editor.Select(0, 6);
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.Equal("A long", editor.SelectedText);
            Assert.Equal(1, editor.Opacity);
            Assert.Equal(Visibility.Collapsed, cell.Highlight.Visibility);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, editor));
            Assert.False(ApplicationCommands.Cut.CanExecute(null, editor));
            editor.ScrollToHorizontalOffset(100);
            editor.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            editor.UpdateLayout();
            Assert.True(host.ScrollableWidth > 0);
            Assert.True(host.HorizontalOffset > 0, "A minimal text host must retain native horizontal selection scrolling.");
            MovePointer(window, other.PointToScreen(new Point(8, other.ActualHeight / 2)));
            other.Focus();
            // Mouse hover/template triggers settle asynchronously after native
            // SetCursorPos and a focus/layout change. A single 25ms frame is not
            // a completion signal on a loaded Windows CI runner.
            PumpUntil(window, () => !editor.IsKeyboardFocusWithin && !editor.IsMouseOver
                && !editor.IsMouseCaptured && host.Visibility == Visibility.Collapsed,
                () => $"Idle text host did not collapse (hover={editor.IsMouseOver}, capture={editor.IsMouseCaptured}, "
                    + $"stylus={editor.IsStylusOver}, touches={editor.AreAnyTouchesOver}, host={host.Visibility}); {InputDiagnostics(window)}");
            Assert.Equal(0, editor.Opacity);
            Assert.Equal(Visibility.Visible, cell.Highlight.Visibility);
            Assert.Equal(value.Value, editor.Text);
            Assert.Equal(Visibility.Collapsed, host.Visibility);

            // Tab-style keyboard traversal activates the same native editor,
            // even when the pointer is over another control.
            Assert.True(other.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            PumpInput(window);
            Assert.True(editor.IsKeyboardFocusWithin);
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(0, rowToggleRequests);
            Assert.False(ApplicationCommands.Cut.CanExecute(null, editor));

            // A never-activated recycled cell must also work on its very first
            // native drag. Do not use cell.TextBox here: that accessor explicitly
            // prepares a native editor for programmatic clients.
            MovePointer(window, other.PointToScreen(new Point(8, other.ActualHeight / 2)));
            other.Focus(); PumpInput(window);
            var fresh = new SelectableCatalogText(new Binding(nameof(CellValue.Value)) { Mode = BindingMode.OneWay })
            {
                DataContext = value, Width = 180, VerticalAlignment = VerticalAlignment.Center,
            };
            // UIElementCollection's indexer does not detach the previous
            // visual. Remove it first, then occupy the now-free visual slot.
            panel.Children.RemoveAt(0);
            panel.Children.Insert(0, fresh);
            PumpInput(window);
            var freshEditor = Assert.Single(fresh.Children.OfType<Wpf.Ui.Controls.TextBox>());
            Assert.False(fresh.IsNativeEditorPrepared);
            Assert.Empty(Descendants<ScrollViewer>(fresh));
            MovePointer(window, freshEditor.PointToScreen(new Point(3, freshEditor.ActualHeight / 2)));
            PumpUntil(window, () => fresh.IsNativeEditorPrepared,
                () => "First hover did not prepare the native editor; " + InputDiagnostics(window));
            SendMouse(window, MouseLeftDown); pointerDown = true; PumpInput(window);
            MovePointer(window, freshEditor.PointToScreen(new Point(90, freshEditor.ActualHeight / 2)));
            PumpInput(window); SendMouse(window, MouseLeftUp); pointerDown = false; PumpInput(window);
            Assert.True(freshEditor.SelectionLength > 0);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, freshEditor));
            Assert.Equal(0, rowToggleRequests);

            // Keyboard entry prepares the missing native view before WPF's
            // TextEditor keyboard-focus handler needs it, without a hover.
            MovePointer(window, other.PointToScreen(new Point(8, other.ActualHeight / 2)));
            other.Focus(); PumpInput(window);
            var keyboardCell = new SelectableCatalogText(new Binding(nameof(CellValue.Value)) { Mode = BindingMode.OneWay })
            {
                DataContext = value, Width = 180, VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.RemoveAt(0);
            panel.Children.Insert(0, keyboardCell);
            PumpInput(window);
            Assert.False(keyboardCell.IsNativeEditorPrepared);
            Assert.True(other.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            PumpInput(window);
            Assert.True(keyboardCell.IsNativeEditorPrepared);
            var keyboardEditor = Assert.Single(keyboardCell.Children.OfType<Wpf.Ui.Controls.TextBox>());
            Assert.True(keyboardEditor.IsKeyboardFocusWithin);
            keyboardEditor.Select(0, 6);
            Assert.Equal("A long", keyboardEditor.SelectedText);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, keyboardEditor));
        }
        finally
        {
            if (pointerDown) _ = SendInput(1, [CreateMouseInput(MouseLeftUp)], Marshal.SizeOf<NativeInput>());
            window.Close();
            if (hasOriginalPointer) SetCursorPos(originalPointer.X, originalPointer.Y);
        }
    }

    private static void MovePointer(Window window, Point point)
    {
        var moved = SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
        var error = Marshal.GetLastWin32Error();
        Assert.True(moved, $"SetCursorPos returned false (Win32={error}); {InputDiagnostics(window)}");
    }

    private static void SendMouse(Window window, uint flags)
    {
        var sent = SendInput(1, [CreateMouseInput(flags)], Marshal.SizeOf<NativeInput>());
        var error = Marshal.GetLastWin32Error();
        Assert.True(sent == 1,
            $"SendInput({flags:X}) returned {sent}/1 (Win32={error}); {InputDiagnostics(window)}");
    }

    private static NativeInput CreateMouseInput(uint flags) => new()
    {
        Type = 0,
        Mouse = new NativeMouseInput { Dx = 0, Dy = 0, MouseData = 0, Flags = flags, Time = 0, ExtraInfo = UIntPtr.Zero },
    };

    private static string InputDiagnostics(Window window) =>
        $"windowActive={window.IsActive}, foreground=0x{GetForegroundWindow():X}, " +
        $"window=0x{new WindowInteropHelper(window).Handle:X}, keyboardFocus={Keyboard.FocusedElement?.GetType().Name ?? "none"}, " +
        $"mouseOver={Mouse.DirectlyOver?.GetType().Name ?? "none"}";

    private static void PumpInput(Window window)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        window.UpdateLayout();
    }

    private static void PumpUntil(Window window, Func<bool> condition, Func<string> diagnostics)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            Mouse.Synchronize();
            PumpInput(window);
        }
        Assert.True(condition(), diagnostics());
    }

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    // MOUSEINPUT is the largest member of INPUT's union on both x86 and x64.
    // Sequential layout supplies the native pointer-alignment padding.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public NativeMouseInput Mouse; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed record CellValue(string Value);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
