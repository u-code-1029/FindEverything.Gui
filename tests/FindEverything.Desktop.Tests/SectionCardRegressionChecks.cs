using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.Views.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>
/// Run these checks on the composition smoke test's application dispatcher.
/// WPF-UI theme resources must not be initialized by competing STA threads.
/// </summary>
internal static class SectionCardRegressionChecks
{
    internal static void Verify()
    {
        VerifyCollapsedHeaderInheritance();
        VerifyHeaderReplacementAndRemoval();
        VerifyFullHeightHeaderAlignment();
    }

    private static void VerifyFullHeightHeaderAlignment()
    {
        var title = new TextBlock { Text = "Preview", Height = 28 };
        var editor = new Wpf.Ui.Controls.TextBox { Text = "Example", IsReadOnly = true };
        var header = new Grid();
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(title);
        Grid.SetRow(editor, 1);
        header.Children.Add(editor);
        var card = new SectionCard
        {
            Header = header,
            Style = (Style)System.Windows.Application.Current.FindResource("FluentCardStyle"),
        };
        card.Measure(new Size(480, 400));
        card.Arrange(new Rect(0, 0, 480, 400));
        card.UpdateLayout();
        FlushBindings();
        card.UpdateLayout();
        var presenter = Assert.IsType<ContentPresenter>(card.Template.FindName("HeaderContentPresenter", card));
        Assert.Equal(VerticalAlignment.Stretch, presenter.VerticalAlignment);
        Assert.InRange(title.TranslatePoint(new Point(), card).Y, 16, 18);
        Assert.True(header.ActualHeight >= card.ActualHeight - 36,
            "A full-height native card must stretch its Header to the content area.");
        Assert.True(editor.ActualHeight >= 330, "The preview editor must use the available vertical space.");
        card.VerticalContentAlignment = VerticalAlignment.Top;
        FlushBindings();
        Assert.Equal(VerticalAlignment.Top, presenter.VerticalAlignment);
        Assert.Same(card, LogicalTreeHelper.GetParent(header));
    }

    private static void VerifyCollapsedHeaderInheritance()
    {
        var first = new HeaderViewModel(new TestCommand());
        var second = new HeaderViewModel(new TestCommand());
        var button = new Wpf.Ui.Controls.Button();
        button.SetBinding(Wpf.Ui.Controls.Button.CommandProperty, new Binding(nameof(HeaderViewModel.Command)));
        var label = new TextBlock();
        label.SetResourceReference(TextBlock.TextProperty, "ProfileLabel");
        var header = new StackPanel();
        header.Children.Add(button);
        header.Children.Add(label);
        var card = new SectionCard { Header = header, Visibility = Visibility.Collapsed };
        var root = new Grid { DataContext = first };
        root.Resources["ProfileLabel"] = "프로필";
        root.Children.Add(card);

        FlushBindings();
        Assert.False(card.IsLoaded);
        Assert.Null(VisualTreeHelper.GetParent(header));
        Assert.Same(card, LogicalTreeHelper.GetParent(header));
        Assert.Same(first, button.DataContext);
        Assert.Same(first.Command, button.Command);
        Assert.Equal("프로필", label.Text);

        root.DataContext = second;
        root.Resources["ProfileLabel"] = "Profile";
        FlushBindings();
        Assert.Same(second, button.DataContext);
        Assert.Same(second.Command, button.Command);
        Assert.Equal("Profile", label.Text);
    }

    private static void VerifyHeaderReplacementAndRemoval()
    {
        var viewModel = new HeaderViewModel(new TestCommand());
        var original = new StackPanel();
        var replacement = new StackPanel();
        var action = new System.Windows.Controls.Button();
        var card = new SectionCard { Header = original, Content = action, DataContext = viewModel };

        Assert.Same(card, LogicalTreeHelper.GetParent(original));
        Assert.Same(card, LogicalTreeHelper.GetParent(action));
        Assert.Equal(new object[] { original, action }, LogicalTreeHelper.GetChildren(card).Cast<object>().ToArray());

        card.Header = replacement;
        FlushBindings();
        Assert.Null(LogicalTreeHelper.GetParent(original));
        Assert.Same(card, LogicalTreeHelper.GetParent(replacement));
        Assert.Same(viewModel, replacement.DataContext);
        Assert.Equal(new object[] { replacement, action }, LogicalTreeHelper.GetChildren(card).Cast<object>().ToArray());

        card.Header = null;
        Assert.Null(LogicalTreeHelper.GetParent(replacement));
        Assert.Same(card, LogicalTreeHelper.GetParent(action));
        Assert.Equal(new object[] { action }, LogicalTreeHelper.GetChildren(card).Cast<object>().ToArray());
    }

    private static void FlushBindings()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed record HeaderViewModel(ICommand Command);

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
