using FindEverything.Application.Options;
using FindEverything.Desktop.Services;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class OperationNotificationServiceTests
{
    [Fact]
    public async Task Notification_uses_three_second_default_instead_of_a_call_specific_timeout()
    {
        var time = new ManualTimeProvider();
        using var service = Create(time);
        service.Show("Saved", "Complete", ControlAppearance.Success, null, TimeSpan.FromMinutes(1));
        Assert.True(service.IsOpen);
        Assert.Equal(InfoBarSeverity.Success, service.Severity);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(service.IsOpen);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilClosedAsync(service);
    }

    [Fact]
    public async Task Replacing_a_notification_restarts_the_timer_without_an_old_timer_closing_it()
    {
        var time = new ManualTimeProvider();
        using var service = Create(time);
        service.Show("First", "First message", ControlAppearance.Secondary, null, TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(2));
        service.Show("Second", "Second message", ControlAppearance.Danger, null, TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(service.IsOpen);
        Assert.Equal("Second", service.Title);
        Assert.Equal("Second message", service.Message);
        Assert.Equal(InfoBarSeverity.Error, service.Severity);

        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilClosedAsync(service);
    }

    [Fact]
    public void Dismiss_closes_immediately_and_cancels_the_pending_timer()
    {
        var time = new ManualTimeProvider();
        using var service = Create(time);
        service.Show("Saved", "Complete", ControlAppearance.Success, null, TimeSpan.Zero);
        service.DismissCommand.Execute(null);

        Assert.False(service.IsOpen);
        Assert.Equal(0, time.ActiveTimerCount);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(service.IsOpen);
    }

    [Fact]
    public async Task Saved_duration_is_read_for_each_notification_without_restarting_the_app()
    {
        var time = new ManualTimeProvider();
        var settings = new MutableSettings();
        using var service = new OperationNotificationService(settings, ImmediateUiScheduler.Instance, time);
        settings.Current = new NotificationOptions { AutoDismissSeconds = 7 };
        service.Show("Saved", "Complete", ControlAppearance.Success, null, TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(service.IsOpen);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilClosedAsync(service);
    }

    [Fact]
    public void Notifications_are_dispatched_to_the_ui_scheduler_and_do_not_require_a_window()
    {
        var scheduler = new QueuedScheduler();
        using var service = new OperationNotificationService(new MutableSettings(), scheduler, new ManualTimeProvider());
        service.Show("Saved", "Complete", ControlAppearance.Success, null, TimeSpan.Zero);
        Assert.False(service.IsOpen);
        scheduler.Flush();
        Assert.True(service.IsOpen);
        service.DismissCommand.Execute(null);
        Assert.True(service.IsOpen);
        scheduler.Flush();
        Assert.False(service.IsOpen);
    }

    private static OperationNotificationService Create(ManualTimeProvider time) =>
        new(new MutableSettings(), ImmediateUiScheduler.Instance, time);

    private static async Task WaitUntilClosedAsync(OperationNotificationService service)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (service.IsOpen)
        {
            Assert.True(DateTime.UtcNow < deadline, "The notification did not dismiss after its timer completed.");
            await Task.Delay(10);
        }
    }

    private sealed class MutableSettings : IValidatedSettingsState<NotificationOptions>
    {
        public NotificationOptions Current { get; set; } = new();
    }

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly Queue<Action> _actions = new();
        public void Post(Action action) => _actions.Enqueue(action);
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
        public void Flush()
        {
            while (_actions.TryDequeue(out var action)) action();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public int ActiveTimerCount => _timers.Count(timer => !timer.IsDisposed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_elapsed);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private TimeSpan _due = owner._elapsed + dueTime;
            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed) return false;
                _due = owner._elapsed + dueTime;
                return true;
            }

            public void FireIfDue(TimeSpan elapsed)
            {
                if (IsDisposed || elapsed < _due) return;
                IsDisposed = true;
                callback(state);
            }

            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
