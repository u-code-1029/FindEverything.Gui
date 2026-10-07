using FindEverything.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ScanCompletionNotifierTests
{
    [Fact]
    public void Notify_uses_the_native_notification_and_taskbar_attention_channels()
    {
        var notificationSink = new RecordingNotificationSink();
        var taskbar = new RecordingTaskbarAttentionService();
        var notifier = new WindowsScanCompletionNotifier(
            notificationSink,
            taskbar,
            NullLogger<WindowsScanCompletionNotifier>.Instance);

        notifier.Notify(new ScanCompletionNotice(
            "빠른 불러오기 완료",
            "프로필 규칙에 맞는 12개 폴더를 찾았습니다."));

        Assert.Equal(1, notificationSink.CallCount);
        Assert.Equal("빠른 불러오기 완료", notificationSink.Title);
        Assert.Equal(1, taskbar.CallCount);
    }

    [Fact]
    public void Notify_does_not_propagate_optional_notification_failures()
    {
        var notifier = new WindowsScanCompletionNotifier(
            new ThrowingNotificationSink(),
            new ThrowingTaskbarAttentionService(),
            NullLogger<WindowsScanCompletionNotifier>.Instance);

        var exception = Record.Exception(() => notifier.Notify(
            new ScanCompletionNotice("완료", "테스트")));

        Assert.Null(exception);
    }

    private sealed class RecordingNotificationSink : IWindowsAppNotificationSink
    {
        public int CallCount { get; private set; }

        public string? Title { get; private set; }

        public bool TryShow(string title, string message)
        {
            CallCount++;
            Title = title;
            return true;
        }
    }

    private sealed class RecordingTaskbarAttentionService : ITaskbarAttentionService
    {
        public int CallCount { get; private set; }

        public void RequestAttention() => CallCount++;
    }

    private sealed class ThrowingNotificationSink : IWindowsAppNotificationSink
    {
        public bool TryShow(string title, string message) =>
            throw new InvalidOperationException("Notifications unavailable.");
    }

    private sealed class ThrowingTaskbarAttentionService : ITaskbarAttentionService
    {
        public void RequestAttention() =>
            throw new InvalidOperationException("No shell window.");
    }
}
