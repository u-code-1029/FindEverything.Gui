using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Profile.Runtime;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.ViewModels;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs on the composition smoke application's existing STA.</summary>
internal static class LiveFileUiChecks
{
    public static void Verify()
    {
        var reads = new ControlledDirectories();
        var browser = new LiveDirectoryBrowser(reads, new TestAppLocalizer());
        var layout = new Grid();
        layout.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        var trigger = new Ui.Button { Content = "Live folder contents", Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        layout.Children.Add(trigger);
        var flyout = new Ui.Flyout { Content = browser, Width = 0, Height = 0, MinWidth = 0, MinHeight = 0 };
        flyout.Closed += (_, _) => browser.Stop();
        layout.Children.Add(flyout);
        var window = new Window { Content = layout, Width = 600, Height = 620, ShowInTaskbar = false };
        window.Show();
        try
        {
            Pump(); Assert.Empty(reads.Requests);
            flyout.ApplyTemplate();
            var popup = Assert.IsType<Popup>(flyout.Template.FindName("PART_Popup", flyout));
            popup.PlacementTarget = trigger; popup.StaysOpen = false;
            flyout.Show();
            PumpUntil(() => popup.IsOpen && browser.IsLoaded && browser.RenderSize.Height > 0);
            Assert.Empty(reads.Requests);
            _ = browser.OpenAsync(@"C:\first"); Pump(); Assert.Single(reads.Requests);
            // Same dispatcher-turn close/reopen mirrors switching row icons.
            flyout.Hide(); browser.Stop(); flyout.Show();
            _ = browser.OpenAsync(@"C:\second"); Pump(); Assert.Equal(2, reads.Requests.Count);
            reads.Requests[0].SetResult(new([new("stale.txt", @"C:\first\stale.txt", false, false)], 1, false));
            reads.Requests[1].SetResult(new([new("current.txt", @"C:\second\current.txt", false, false)], 1, false));
            var tree = Descendants(browser).OfType<TreeView>().Single();
            var root = Assert.IsType<Ui.TreeViewItem>(Assert.Single(tree.Items.Cast<object>()));
            PumpUntil(() => root.Items.Count == 1 && root.Items[0] is TreeViewItem item
                && Equals(item.ToolTip, @"C:\second\current.txt"));
            Assert.Single(root.Items); Assert.Equal(@"C:\second\current.txt", ((TreeViewItem)root.Items[0]).ToolTip);
            VerifyPageRetry(browser, tree, reads);
            SaveQa(popup.Child as FrameworkElement, "live-folder-browser.png");
            flyout.Hide(); PumpUntil(() => !popup.IsOpen && !browser.IsLoaded);
            Assert.NotNull(root.Items[0]);
        }
        finally { flyout.Hide(); window.Close(); browser.Stop(); }

        VerifyCurrentRawFileNameValues();

        var field = new ProfileTextFileFieldDescriptor(0, "note", "Note", @"note\.txt", ProfileRegexMatchMode.Full, true, 100, false, 4096)
        { EditOptions = new() { Enabled = true, CreationFileNameTemplate = "note.txt" } };
        var document = new TextFileEditDocument(@"C:\archive", @"C:\archive\note.txt", "editable contents", false,
            field, new UTF8Encoding(false, true), false, "\r\n", "version");
        var presenter = new Ui.ContentDialogHost();
        var editorLayout = new Grid(); editorLayout.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        editorLayout.Children.Add(new Ui.TextBlock { Text = "Structured results · file editor", Margin = new Thickness(24), FontSize = 24 });
        editorLayout.Children.Add(presenter);
        var editorWindow = new Window { Content = editorLayout, Width = 760, Height = 650, ShowInTaskbar = false };
        editorWindow.Show();
        var saves = 0; var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new TextFileEditorDialog(document, new TestAppLocalizer(), _ => { saves++; return completion.Task; });
        var dialogs = new Wpf.Ui.ContentDialogService(); dialogs.SetDialogHost(presenter);
        var showing = dialogs.ShowAsync(dialog, CancellationToken.None);
        try
        {
            PumpUntil(() => dialog.IsLoaded && dialog.IsVisible && dialog.RenderSize.Width > 0 && dialog.RenderSize.Height > 0);
            Assert.Same(dialog, presenter.Content);
            Assert.NotNull(PresentationSource.FromVisual(dialog));
            Assert.NotNull(dialog.Style); Assert.Equal(typeof(Ui.ContentDialog), dialog.Style.TargetType);
            var editor = Descendants(dialog).OfType<Ui.TextBox>().Single();
            var primary = Assert.IsType<Ui.Button>(dialog.Template.FindName("PrimaryButton", dialog));
            var close = Assert.IsType<Ui.Button>(dialog.Template.FindName("CloseButton", dialog));
            var path = Descendants(dialog).OfType<Ui.TextBlock>().Single(text => text.Name == "FileTargetPath");
            var error = Descendants(dialog).OfType<Ui.TextBlock>().Single(text => text.Name == "FileEditError");
            Assert.False(editor.IsReadOnly); Assert.True(editor.AcceptsReturn); Assert.Equal("editable contents", editor.Text);
            Assert.Equal(ScrollBarVisibility.Auto, editor.VerticalScrollBarVisibility);
            editor.Focus(); editor.SelectAll(); editor.SetCurrentValue(TextBox.TextProperty, "unsaved draft\r\nsecond line");
            Pump(); editorLayout.UpdateLayout();
            Assert.Equal(document.FilePath, path.Text); AssertVisibleBounds(path, editorLayout);
            AssertFooterButtons(dialog, editorLayout);
            SaveQa(editorLayout, "text-file-editor.png");
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary);
            Assert.Equal(1, saves); Assert.False(primary.IsEnabled); Assert.False(close.IsEnabled); Assert.True(editor.IsReadOnly);
            AssertFooterButtons(dialog, editorLayout);
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary); Assert.Equal(1, saves);
            Assert.True(close.IsCancel); // WPF Escape invokes this native cancel-button command.
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Close);
            Assert.False(showing.IsCompleted); Assert.Same(dialog, presenter.Content);
            completion.SetResult(false); PumpUntil(() => primary.IsEnabled);
            Assert.False(editor.IsReadOnly); Assert.False(showing.IsCompleted); Assert.Same(dialog, presenter.Content);
            Assert.Equal("unsaved draft\r\nsecond line", editor.Text);

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary); Assert.Equal(2, saves);
            completion.SetException(new System.IO.IOException("The file changed externally. Reopen it before saving."));
            PumpUntil(() => primary.IsEnabled);
            Assert.False(showing.IsCompleted); Assert.False(editor.IsReadOnly);
            Assert.Equal("unsaved draft\r\nsecond line", editor.Text);
            Assert.Contains(Descendants(dialog).OfType<Ui.TextBlock>(), text => text.Text.Contains("changed externally", StringComparison.Ordinal));
            editorLayout.UpdateLayout();
            AssertVisibleBounds(path, editorLayout); AssertVisibleBounds(error, editorLayout);
            AssertFooterButtons(dialog, editorLayout);
            SaveQa(editorLayout, "text-file-editor-draft-preserved.png");

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary); Assert.Equal(3, saves);
            completion.SetResult(true); PumpUntil(() => showing.IsCompleted && presenter.Content is null);
            Assert.Equal(Ui.ContentDialogResult.Primary, showing.GetAwaiter().GetResult());
        }
        finally { dialog.Hide(); editorWindow.Close(); }
    }

    private static void AssertFooterButtons(TextFileEditorDialog dialog, FrameworkElement scene)
    {
        dialog.UpdateLayout();
        var primary = Assert.IsType<Ui.Button>(dialog.Template.FindName("PrimaryButton", dialog));
        var secondary = Assert.IsType<Ui.Button>(dialog.Template.FindName("SecondaryButton", dialog));
        var close = Assert.IsType<Ui.Button>(dialog.Template.FindName("CloseButton", dialog));
        Assert.False(dialog.IsSecondaryButtonEnabled); Assert.Equal(Visibility.Collapsed, secondary.Visibility);
        Assert.Equal(dialog.PrimaryButtonText, primary.Content); Assert.Equal(dialog.CloseButtonText, close.Content);
        AssertVisibleBounds(primary, scene); AssertVisibleBounds(close, scene);
        Assert.Equal(2, Descendants(dialog).OfType<Ui.Button>().Count(button =>
            ReferenceEquals(button.Command, dialog.TemplateButtonCommand) && button.IsVisible));
    }

    private static void AssertVisibleBounds(FrameworkElement element, FrameworkElement scene)
    {
        scene.UpdateLayout();
        Assert.True(element.IsVisible && element.Opacity > 0);
        Assert.True(element.RenderSize.Width > 0 && element.RenderSize.Height > 0);
        var bounds = element.TransformToAncestor(scene).TransformBounds(new Rect(element.RenderSize));
        var viewport = new Rect(scene.RenderSize);
        viewport.Inflate(0.5, 0.5); // Native DPI/layout rounding may differ by half a DIP.
        Assert.True(viewport.Contains(bounds), $"{element.Name} is outside the visible dialog scene: {bounds} in {viewport}.");
        // Being connected/Visible is not enough: a logical/pixel scroll offset
        // can put content entirely outside the native scroll presenter clip.
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null && !ReferenceEquals(parent, scene);
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollContentPresenter content) continue;
            var inScroll = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            var clip = new Rect(content.RenderSize);
            clip.Inflate(0.5, 0.5);
            Assert.True(clip.Contains(inScroll), $"{element.Name} is clipped by its native scroll viewport: {inScroll} in {clip}.");
        }
    }

    private static void VerifyCurrentRawFileNameValues()
    {
        var fields = new[]
        {
            new ProfileFieldDescriptor("name", "", "Name", 0, false, ProfileFieldValueKind.String, true, null, null)
                { SourceKind = ProfileFieldSourceKind.UserInput, DefaultValue = "Old" },
            new ProfileFieldDescriptor("alias", "alias", "Alias", 1, false, ProfileFieldValueKind.String, true, null, null)
                { ValueMappings = new Dictionary<string, string> { ["A"] = "Mapped name" } },
            new ProfileFieldDescriptor("date", "date", "Date", 2, false, ProfileFieldValueKind.DateTime, true, "yyyy-MM-dd", "MM/dd/yyyy"),
        };
        var values = new Dictionary<string, object?> { ["name"] = "Old", ["alias"] = "A", ["date"] = new DateTime(2026, 5, 21) };
        var row = new CatalogItemViewModel(new CatalogItem(@"C:\archive", "archive", "rule", new object(), values, false),
            fields, new TestAppLocalizer());
        row.UserInputValues["name"].Value = "New";
        Assert.Equal("Old", row.Values["name"]); // The immutable provider page remains unchanged.
        var captured = row.CaptureFileNameValues();
        Assert.Equal("New_A_2026-05-21.txt", TextFileEditingService.FormatCreationName(
            "{Field:name}_{Field:alias}_{Field:date:yyyy-MM-dd}.txt", captured));
        Assert.IsType<DateTime>(captured["date"]);
        row.UserInputValues["name"].Value = "Later";
        Assert.Equal("New", captured["name"]); // Opening an editor takes one immutable snapshot.
        row.SetShowOriginalValues(true);
        Assert.Equal("Later_A.txt", TextFileEditingService.FormatCreationName("{Field:name}_{Field:alias}.txt", row.CaptureFileNameValues()));
    }

    private static void VerifyPageRetry(LiveDirectoryBrowser browser, TreeView tree, ControlledDirectories reads)
    {
        _ = browser.OpenAsync(@"C:\paged"); PumpUntil(() => reads.Requests.Count == 3);
        reads.Requests[2].SetResult(new([new("first.txt", @"C:\paged\first.txt", false, false)], 256, true));
        var root = Assert.IsType<Ui.TreeViewItem>(Assert.Single(tree.Items.Cast<object>()));
        PumpUntil(() => root.Items.OfType<TreeViewItem>().Any(item => item.Header is Ui.Button));
        var more = (Ui.Button)root.Items.OfType<TreeViewItem>().Single(item => item.Header is Ui.Button).Header;
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(4, reads.Requests.Count); Assert.False(more.IsEnabled);
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(4, reads.Requests.Count);
        reads.Requests[3].SetException(new System.IO.IOException("Temporary network error"));
        PumpUntil(() => more.IsEnabled && root.Items.OfType<TreeViewItem>().Any(item => Equals(item.ToolTip, "Temporary network error")));
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(5, reads.Requests.Count);
        Assert.Equal(256, reads.Offsets[3]); Assert.Equal(256, reads.Offsets[4]);
        reads.Requests[4].SetResult(new([new("second.txt", @"C:\paged\second.txt", false, false)], 257, false));
        PumpUntil(() => root.Items.Count == 2 && root.Items.OfType<TreeViewItem>().All(item => item.Header is not Ui.Button));
        Assert.Equal(new[] { @"C:\paged\first.txt", @"C:\paged\second.txt" }, root.Items.OfType<TreeViewItem>().Select(item => item.ToolTip).Cast<string>().ToArray());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(3)) { Pump(); Thread.Sleep(1); }
        Assert.True(condition(), "The existing dispatcher did not adopt the completed live operation.");
    }
    private static void SaveQa(FrameworkElement? element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("FINDEVERYTHING_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory) || element is null) return;
        element.UpdateLayout();
        Assert.True(element.RenderSize.Width > 0 && element.RenderSize.Height > 0);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.RenderSize.Width), (int)Math.Ceiling(element.RenderSize.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(directory);
        using var stream = new System.IO.FileStream(System.IO.Path.Combine(directory, name), System.IO.FileMode.Create, System.IO.FileAccess.Write);
        encoder.Save(stream);
    }
    private sealed class ControlledDirectories : ILiveDirectoryService
    {
        public List<TaskCompletionSource<LiveDirectoryPage>> Requests { get; } = [];
        public List<int> Offsets { get; } = [];
        public Task<LiveDirectoryPage> ReadPageAsync(string path, int offset = 0, CancellationToken cancellationToken = default)
        {
            var result = new TaskCompletionSource<LiveDirectoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(result); Offsets.Add(offset); return result.Task;
        }
    }
}
