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
            Assert.False(editor.IsReadOnly); Assert.True(editor.AcceptsReturn); Assert.Equal("editable contents", editor.Text);
            Assert.Equal(ScrollBarVisibility.Auto, editor.VerticalScrollBarVisibility);
            editor.Focus(); editor.SelectAll(); editor.SetCurrentValue(TextBox.TextProperty, "unsaved draft\r\nsecond line");
            SaveQa(editorLayout, "text-file-editor.png");
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary);
            Assert.Equal(1, saves); Assert.False(dialog.IsPrimaryButtonEnabled); Assert.True(editor.IsReadOnly);
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary); Assert.Equal(1, saves);
            completion.SetResult(false); PumpUntil(() => dialog.IsPrimaryButtonEnabled);
            Assert.False(editor.IsReadOnly); Assert.False(showing.IsCompleted); Assert.Same(dialog, presenter.Content);
            Assert.Equal("unsaved draft\r\nsecond line", editor.Text);

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary); Assert.Equal(2, saves);
            completion.SetException(new System.IO.IOException("The file changed externally. Reopen it before saving."));
            PumpUntil(() => dialog.IsPrimaryButtonEnabled);
            Assert.False(showing.IsCompleted); Assert.False(editor.IsReadOnly);
            Assert.Equal("unsaved draft\r\nsecond line", editor.Text);
            Assert.Contains(Descendants(dialog).OfType<Ui.TextBlock>(), text => text.Text.Contains("changed externally", StringComparison.Ordinal));
            SaveQa(editorLayout, "text-file-editor-draft-preserved.png");

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.TemplateButtonCommand.Execute(Ui.ContentDialogButton.Primary); Assert.Equal(3, saves);
            completion.SetResult(true); PumpUntil(() => showing.IsCompleted && presenter.Content is null);
            Assert.Equal(Ui.ContentDialogResult.Primary, showing.GetAwaiter().GetResult());
        }
        finally { dialog.Hide(); editorWindow.Close(); }
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
        public Task<LiveDirectoryPage> ReadPageAsync(string path, int offset = 0, CancellationToken cancellationToken = default)
        {
            var result = new TaskCompletionSource<LiveDirectoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(result); return result.Task;
        }
    }
}
