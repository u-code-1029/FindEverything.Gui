using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Dialogs;
using FindEverything.Profile.Runtime;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Uses the smoke test's existing application and STA.</summary>
internal static class SelectionImportUiChecks
{
    public static void Verify()
    {
        var rule = new ProfileSelectionMatchRuleManifest { Id = "line", DisplayName = "Line match", Columns = [new() { FieldId = "alias" }] };
        using var lifetime = new CancellationTokenSource();
        var matched = 0;
        using var model = new SelectionImportViewModel([rule], new TestAppLocalizer(), async (_, document, token) =>
        {
            var rows = new List<CatalogSelectionInputRow>();
            await foreach (var row in document.ReadRowsAsync(token)) rows.Add(row);
            matched++; Assert.Equal("Alice", Assert.Single(rows).Values[0]);
            return new CatalogSelectionMatchResult(new Keys(), new(1, 1, 0, 0, 1, 0, 0, 2, []));
        }, lifetime.Token);
        var dialog = new SelectionImportDialog(model) { Width = 700, Height = 620 };
        try
        {
            dialog.Show(); dialog.UpdateLayout(); Pump();
            Assert.NotNull(dialog.Style); Assert.Equal(typeof(Ui.FluentWindow), dialog.Style.TargetType);
            var cards = Descendants(dialog).OfType<Ui.CardControl>().ToArray(); Assert.Equal(3, cards.Length);
            Assert.All(cards, card => { Assert.IsType<StackPanel>(card.Header); Assert.Null(card.Content); });
            var boxes = Descendants(dialog).OfType<Ui.TextBox>().ToArray();
            var input = Assert.Single(boxes, box => box.AcceptsReturn);
            Assert.True(input.ActualWidth > 400); Assert.False(input.IsReadOnly);
            Assert.Equal(ScrollBarVisibility.Auto, input.VerticalScrollBarVisibility);
            Assert.False(model.ApplyCommand.CanExecute(null));
            model.InputText = "Alice";
            Assert.True(model.PreviewCommand.CanExecute(null));
            var preview = model.PreviewCommand.ExecuteAsync(null);
            PumpUntil(() => preview.IsCompleted);
            preview.GetAwaiter().GetResult(); Assert.Equal(1, matched); Assert.True(model.CanApply); Assert.Single(model.PreviewRows);
            model.InputText = "Bob"; Assert.False(model.CanApply); Assert.Empty(model.PreviewRows);
        }
        finally { dialog.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
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
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(1); }
        Assert.True(condition(), "Data selection preview did not finish on the existing dispatcher.");
    }
    private sealed class Keys : IQuerySelectionKeySet
    {
        public long Count => 2;
        public string DatabasePath => "test.db";
        public bool Contains(string key) => true;
        public IQuerySelectionKeySet Retain() => new Keys();
        public void Dispose() { }
    }
}
