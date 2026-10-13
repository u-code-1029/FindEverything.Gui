using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class PageActivationTests
{
    [Fact]
    public Task Repeated_load_is_idempotent_and_unload_cancels_without_disposing_retained_session() => RunStaAsync(() =>
    {
        var session = new Session();
        var page = new Border { DataContext = session };
        PageActivation.Attach(page);
        PageActivation.Attach(page);

        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(1, session.Activations);
        var firstToken = session.LastToken;
        Assert.False(firstToken.IsCancellationRequested);

        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(1, session.Deactivations);
        Assert.False(session.Disposed);

        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(2, session.Activations);
        Assert.False(session.LastToken.IsCancellationRequested);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    [Fact]
    public Task Unload_cancels_incomplete_activation_and_observes_its_exception() => RunStaAsync(() =>
    {
        var session = new Session { BlockActivation = true };
        var page = new Border { DataContext = session };
        PageActivation.Attach(page);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        DrainDispatcher();
        Assert.True(session.LastToken.IsCancellationRequested);
        Assert.Equal(1, session.Deactivations);

        page.DataContext = new Session { FailActivation = true };
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        DrainDispatcher();
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    });

    private static async Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class Session : IPageSession
    {
        public bool BlockActivation { get; init; }
        public bool FailActivation { get; init; }
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            Activations++;
            LastToken = cancellationToken;
            return FailActivation ? Task.FromException(new InvalidOperationException("Activation failed"))
                : BlockActivation ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
        }
        public void Deactivate() => Deactivations++;
        public void Dispose() => Disposed = true;
    }
}
