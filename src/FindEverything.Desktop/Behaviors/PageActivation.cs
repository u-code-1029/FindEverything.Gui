using System.Diagnostics;
using System.Windows;
using FindEverything.Desktop.Services;

namespace FindEverything.Desktop.Behaviors;

/// <summary>Connects a retained view to its asynchronous, visibility-scoped session.</summary>
public static class PageActivation
{
    private static readonly DependencyProperty AttachmentProperty = DependencyProperty.RegisterAttached(
        "Attachment", typeof(Attachment), typeof(PageActivation), new PropertyMetadata(null));

    public static void Attach(FrameworkElement page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.GetValue(AttachmentProperty) is Attachment) return;
        page.SetValue(AttachmentProperty, new Attachment(page));
    }

    private sealed class Attachment
    {
        private readonly FrameworkElement _page;
        private IPageSession? _activeSession;
        private Activation? _activation;

        public Attachment(FrameworkElement page)
        {
            _page = page;
            page.Loaded += OnLoaded;
            page.Unloaded += OnUnloaded;
            page.DataContextChanged += OnDataContextChanged;
            if (page.IsLoaded) Activate();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Activate();

        private void OnUnloaded(object sender, RoutedEventArgs args) => Deactivate();

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            Deactivate();
            if (_page.IsLoaded) Activate();
        }

        private async void Activate()
        {
            if (_activeSession is not null || _page.DataContext is not IPageSession session) return;
            var activation = new Activation();
            _activation = activation;
            _activeSession = session;
            try
            {
                await session.ActivateAsync(activation.Cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (activation.Cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // An async Loaded callback must never raise an unobserved UI
                // exception. Feature sessions display their contextual failures.
                Trace.TraceError("Page activation failed for {0}: {1}", _page.GetType().Name, exception);
            }
            finally
            {
                activation.Completed = true;
                if (!ReferenceEquals(_activation, activation)) activation.Cancellation.Dispose();
            }
        }

        private void Deactivate()
        {
            var session = _activeSession;
            _activeSession = null;
            var activation = _activation;
            _activation = null;
            if (activation is not null)
            {
                try { activation.Cancellation.Cancel(); }
                catch (AggregateException exception)
                {
                    Trace.TraceError("Page activation cancellation failed: {0}", exception);
                }
                finally
                {
                    if (activation.Completed) activation.Cancellation.Dispose();
                }
            }
            if (session is null) return;
            try { session.Deactivate(); }
            catch (Exception exception)
            {
                Trace.TraceError("Page deactivation failed for {0}: {1}", _page.GetType().Name, exception);
            }
        }

        private sealed class Activation
        {
            public CancellationTokenSource Cancellation { get; } = new();
            public bool Completed { get; set; }
        }
    }
}
