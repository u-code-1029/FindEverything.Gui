using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FindEverything.Desktop.Views.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SectionCardTests
{
    [Fact]
    public Task Collapsed_header_inherits_commands_and_live_resources_before_template_creation() => RunOnSta(() =>
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
    });

    [Fact]
    public Task Header_replacement_and_removal_update_the_logical_tree_without_losing_content() => RunOnSta(() =>
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
    });

    private static void FlushBindings()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static Task RunOnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "FindEverything native section card test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed record HeaderViewModel(ICommand Command);

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
