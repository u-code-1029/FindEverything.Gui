using System.IO;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Wpf.Ui.Controls;
using Xunit;
using Xunit.Abstractions;

namespace FindEverything.Desktop.Tests;

public sealed class ScanConsoleViewModelTests
{
    private readonly ITestOutputHelper _output;

    public ScanConsoleViewModelTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Closed_console_retains_a_hundred_thousand_line_burst_without_row_notifications()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { });
        var notifications = 0;
        viewModel.Lines.CollectionChanged += (_, _) => notifications++;
        var trace = CreateEvent(CatalogScanTraceKind.DirectoryVisited, @"C:\Root", @"C:\Root\Item");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();

        viewModel.Report(trace with { Kind = CatalogScanTraceKind.Started, Sequence = 0 });
        for (var sequence = 1; sequence <= 100_000; sequence++)
        {
            viewModel.Report(trace with { Sequence = sequence });
        }

        viewModel.Report(trace with { Kind = CatalogScanTraceKind.Completed, Sequence = 100_001 });
        // Producer retention is already bounded and current even when no UI
        // dispatcher has been pumped for the entire scan.
        Assert.InRange(viewModel.RetainedLineCount, 18_000, 20_000);
        Assert.Equal(0, viewModel.VisitedCount);
        DrainAll(viewModel);
        timer.Stop();
        _output.WriteLine(
            "Closed scan console 100,000 visits: {0:F2} ms, {1:N0} allocated bytes, {2} row notifications, {3:N0} retained lines.",
            timer.Elapsed.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - before,
            notifications,
            viewModel.RetainedLineCount);

        Assert.Empty(viewModel.Lines);
        Assert.Equal(0, notifications);
        Assert.InRange(viewModel.RetainedLineCount, 18_000, 20_000);
        Assert.Equal(100_000, viewModel.VisitedCount);
        Assert.Equal(100_000, viewModel.MatchedCount);
        Assert.Equal("완료", viewModel.StatusText);
        Assert.True(viewModel.IsActivityAttentionRequested);
        Assert.Contains("생략", viewModel.RetentionSummary, StringComparison.Ordinal);
        Assert.True(viewModel.CopyAllCommand.CanExecute(null));
        Assert.True(viewModel.SaveLogCommand.CanExecute(null));

        viewModel.IsPanelOpen = true;

        Assert.Equal(1, notifications);
        Assert.Equal(viewModel.RetainedLineCount, viewModel.Lines.Count);
        Assert.Equal(CatalogScanTraceKind.Completed, viewModel.Lines[^1].Kind);
        Assert.False(viewModel.IsActivityAttentionRequested);
    }

    [Fact]
    public async Task Closing_and_reopening_retains_only_the_current_session_and_allows_hidden_copy_and_save()
    {
        string? copied = null;
        IReadOnlyList<string>? saved = null;
        var viewModel = new ScanConsoleViewModel(
            text => copied = text,
            static _ => @"C:\Logs\closed.log",
            (_, lines, _) =>
            {
                saved = lines;
                return Task.CompletedTask;
            },
            static (_, _, _) => { },
            dispatcher: Dispatcher.CurrentDispatcher) { IsPanelOpen = true };
        var oldTrace = CreateEvent(CatalogScanTraceKind.Started, @"C:\Old");
        viewModel.Report(oldTrace);
        DrainAll(viewModel);
        viewModel.SelectedLine = viewModel.Lines[0];
        viewModel.IsPanelOpen = false;
        var notifications = 0;
        viewModel.Lines.CollectionChanged += (_, _) => notifications++;
        var newTrace = CreateEvent(CatalogScanTraceKind.Started, @"C:\New");
        viewModel.Report(newTrace);
        viewModel.Report(newTrace with
        {
            Kind = CatalogScanTraceKind.DirectoryVisited,
            Sequence = 2,
            FullPath = @"C:\New\Item",
            MatchInput = @"C:\New\Item",
        });
        DrainAll(viewModel);

        Assert.Equal(0, notifications);
        Assert.Null(viewModel.SelectedLine);
        viewModel.CopyAllCommand.Execute(null);
        await viewModel.SaveLogCommand.ExecuteAsync(null);
        Assert.NotNull(copied);
        Assert.Contains(@"C:\New", copied, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Old", copied, StringComparison.Ordinal);
        Assert.Equal(2, saved?.Count);

        viewModel.IsPanelOpen = true;

        Assert.Equal(1, notifications);
        Assert.Equal(2, viewModel.Lines.Count);
        Assert.All(viewModel.Lines, line => Assert.Equal(newTrace.OperationId, line.OperationId));
    }

    [Fact]
    public void Burst_under_the_retention_limit_keeps_start_and_terminal_markers_without_pending_queue_loss()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { });
        var trace = CreateEvent(CatalogScanTraceKind.Started, @"C:\Root");
        viewModel.Report(trace);
        for (var sequence = 1; sequence <= 12_000; sequence++)
        {
            viewModel.Report(trace with { Kind = CatalogScanTraceKind.DirectoryVisited, Sequence = sequence });
        }

        viewModel.Report(trace with { Kind = CatalogScanTraceKind.Cancelled, Sequence = 12_001 });
        Assert.Equal(12_002, viewModel.RetainedLineCount);
        DrainAll(viewModel);
        viewModel.IsPanelOpen = true;

        Assert.Equal(12_002, viewModel.Lines.Count);
        Assert.Equal(CatalogScanTraceKind.Started, viewModel.Lines[0].Kind);
        Assert.Equal(CatalogScanTraceKind.Cancelled, viewModel.Lines[^1].Kind);
        Assert.Equal("취소됨", viewModel.StatusText);
        Assert.DoesNotContain("생략", viewModel.RetentionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_bursts_publish_one_reset_per_batch_instead_of_one_notification_per_folder()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { }) { IsPanelOpen = true };
        var notifications = new List<NotifyCollectionChangedAction>();
        viewModel.Lines.CollectionChanged += (_, args) =>
        {
            notifications.Add(args.Action);
        };
        var trace = CreateEvent(CatalogScanTraceKind.DirectoryVisited, @"C:\Root", @"C:\Root\Item");
        for (var index = 0; index < 10_000; index++)
        {
            viewModel.Report(trace with { Sequence = index });
        }

        var drained = viewModel.DrainPendingLines();

        Assert.Equal(10_000, drained);
        Assert.Equal([NotifyCollectionChangedAction.Reset], notifications);
        Assert.Equal(0, viewModel.PendingLineCount);
        Assert.Equal(10_000, viewModel.Lines.Count);
        Assert.Equal(0, viewModel.DrainPendingLines());
        Assert.Single(notifications);

        for (var index = 10_000; index < 21_000; index++)
            viewModel.Report(trace with { Sequence = index });
        DrainAll(viewModel);
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, action => Assert.Equal(NotifyCollectionChangedAction.Reset, action));
        Assert.Equal(19_000, viewModel.Lines.Count);
        Assert.Equal(2000, viewModel.Lines[0].Sequence);
        Assert.Equal(20_999, viewModel.Lines[^1].Sequence);
    }

    [Fact]
    public void Bulk_trim_keeps_a_retained_selection_even_if_a_reset_listener_clears_it()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { }) { IsPanelOpen = true };
        var trace = CreateEvent(CatalogScanTraceKind.DirectoryVisited, @"C:\Root", @"C:\Root\Item");
        for (var index = 0; index < 20_000; index++)
        {
            viewModel.Report(trace with { Sequence = index });
            if (index % 500 == 0)
            {
                DrainAll(viewModel);
            }
        }

        DrainAll(viewModel);
        var selected = viewModel.Lines[^1];
        viewModel.SelectedLine = selected;
        viewModel.Lines.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedLine = null;
            }
        };

        viewModel.Report(trace with { Sequence = 20_000 });
        DrainAll(viewModel);

        Assert.Same(selected, viewModel.SelectedLine);
        Assert.Equal(18_001, viewModel.Lines.Count);
        Assert.Equal(viewModel.RetainedLineCount, viewModel.Lines.Count);
    }

    private static void DrainAll(ScanConsoleViewModel viewModel)
    {
        while (viewModel.PendingLineCount > 0)
        {
            Assert.InRange(viewModel.DrainPendingLines(), 1, 20_000);
        }
    }

    [Fact]
    public async Task Save_includes_the_current_click_time_retention_before_any_ui_publication()
    {
        IReadOnlyList<string>? saved = null;
        var viewModel = new ScanConsoleViewModel(
            static _ => { }, static _ => @"C:\Logs\current.log",
            (_, lines, _) => { saved = lines; return Task.CompletedTask; },
            static (_, _, _) => { }, dispatcher: Dispatcher.CurrentDispatcher);
        var trace = CreateEvent(CatalogScanTraceKind.Started, @"C:\Root");
        viewModel.Report(trace);
        for (var index = 1; index <= 1_000; index++)
            viewModel.Report(trace with { Kind = CatalogScanTraceKind.DirectoryVisited, Sequence = index });

        Assert.Empty(viewModel.Lines);
        Assert.True(viewModel.SaveLogCommand.CanExecute(null));
        await viewModel.SaveLogCommand.ExecuteAsync(null);

        Assert.Equal(1001, saved?.Count);
        Assert.Contains("[START]", saved![0], StringComparison.Ordinal);
        Assert.Contains("#001000", saved[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_a_pending_session_does_not_replay_old_rows_or_lose_lifecycle_and_counters()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { }) { IsPanelOpen = true };
        var trace = CreateEvent(CatalogScanTraceKind.Started, @"C:\Root");
        viewModel.Report(trace);
        for (var index = 1; index <= 1_000; index++)
            viewModel.Report(trace with { Kind = CatalogScanTraceKind.DirectoryVisited, Sequence = index });
        viewModel.ClearCommand.Execute(null);
        Assert.Empty(viewModel.Lines);
        Assert.Equal(0, viewModel.RetainedLineCount);
        Assert.Equal("탐색 중", viewModel.StatusText);
        Assert.Equal(1_000, viewModel.VisitedCount);
        Assert.False(viewModel.CopyAllCommand.CanExecute(null));
        viewModel.Report(trace with { Kind = CatalogScanTraceKind.Cancelled, Sequence = 1001 });
        DrainAll(viewModel);

        Assert.Equal(CatalogScanTraceKind.Cancelled, Assert.Single(viewModel.Lines).Kind);
        Assert.Equal("취소됨", viewModel.StatusText);
        Assert.Equal(1_000, viewModel.VisitedCount);
    }

    [Fact]
    public void Concurrent_reports_keep_exact_counts_and_a_bounded_retained_window_without_ui_work()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { });
        var trace = CreateEvent(CatalogScanTraceKind.DirectoryVisited, @"C:\Root", @"C:\Root\Item");
        viewModel.Report(trace with { Kind = CatalogScanTraceKind.Started, Sequence = 0 });
        Parallel.For(1, 40_001, index => viewModel.Report(trace with { Sequence = index }));
        Assert.Empty(viewModel.Lines);
        Assert.InRange(viewModel.RetainedLineCount, 18_000, 20_000);
        viewModel.Report(trace with { Kind = CatalogScanTraceKind.Completed, Sequence = 40_001 });
        DrainAll(viewModel);

        Assert.Equal(40_000, viewModel.VisitedCount);
        Assert.Equal(40_000, viewModel.MatchedCount);
        Assert.Equal("완료", viewModel.StatusText);
        viewModel.IsPanelOpen = true;
        Assert.Equal(CatalogScanTraceKind.Completed, viewModel.Lines[^1].Kind);
    }

    [Fact]
    public void Disposal_is_idempotent_and_ignores_reports_and_scheduled_publications()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { });
        var trace = CreateEvent(CatalogScanTraceKind.DirectoryVisited, @"C:\Root", @"C:\Root\Item");
        viewModel.Report(trace);
        viewModel.Dispose();
        viewModel.Dispose();
        Parallel.For(0, 1_000, _ => viewModel.Report(trace));

        Assert.Equal(0, viewModel.RetainedLineCount);
        Assert.Equal(0, viewModel.PendingLineCount);
        Assert.Equal(0, viewModel.DrainPendingLines());
        Assert.Empty(viewModel.Lines);
    }

    [Fact]
    public void Diagnostic_values_use_bounded_previews_without_losing_the_copyable_path()
    {
        var trace = CreateEvent(CatalogScanTraceKind.DirectoryVisited,
            rootPath: @"C:\Root", fullPath: @"C:\Root\Item") with
        {
            Values = new Dictionary<string, object?> { ["notes"] = new string('x', 4 * 1024 * 1024) },
        };
        var line = ScanConsoleViewModel.CreateLine(trace);
        Assert.True(line.Text.Length < 2000);
        Assert.Contains("[preview]", line.Text, StringComparison.Ordinal);
        Assert.Equal(@"C:\Root\Item", line.Path);
        Assert.Equal(4 * 1024 * 1024, ((string)trace.Values["notes"]!).Length);
    }

    [Fact]
    public void Closed_panel_requests_attention_until_the_user_opens_it()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { })
        {
            HasUnseenActivity = true,
        };

        Assert.False(viewModel.IsPanelOpen);
        Assert.True(viewModel.IsActivityAttentionRequested);

        viewModel.TogglePanelCommand.Execute(null);

        Assert.True(viewModel.IsPanelOpen);
        Assert.False(viewModel.HasUnseenActivity);
        Assert.False(viewModel.IsActivityAttentionRequested);
    }

    [Fact]
    public void Trace_lines_keep_the_path_that_the_user_can_copy()
    {
        var started = ScanConsoleViewModel.CreateLine(CreateEvent(
            CatalogScanTraceKind.Started,
            rootPath: @"Z:\Projects"));
        var visited = ScanConsoleViewModel.CreateLine(CreateEvent(
            CatalogScanTraceKind.DirectoryVisited,
            rootPath: @"Z:\Projects",
            fullPath: @"Z:\Projects\2026\0521_Project"));
        var error = ScanConsoleViewModel.CreateLine(CreateEvent(
            CatalogScanTraceKind.DiscoveryError,
            rootPath: @"Z:\Projects",
            fullPath: @"Z:\Projects\Private"));
        var excluded = ScanConsoleViewModel.CreateLine(CreateEvent(
            CatalogScanTraceKind.DirectoryExcluded,
            rootPath: @"Z:\Projects",
            fullPath: @"Z:\Projects\Cache"));
        var completed = ScanConsoleViewModel.CreateLine(CreateEvent(
            CatalogScanTraceKind.Completed,
            rootPath: @"Z:\Projects"));

        Assert.Equal(@"Z:\Projects", started.Path);
        Assert.True(started.HasPath);
        Assert.Equal(@"Z:\Projects\2026\0521_Project", visited.Path);
        Assert.Equal(@"Z:\Projects\Private", error.Path);
        Assert.Equal(@"Z:\Projects\Cache", excluded.Path);
        Assert.Null(completed.Path);
        Assert.False(completed.HasPath);
    }

    [Fact]
    public void Directory_exclusion_is_rendered_as_a_pruned_copyable_path()
    {
        var trace = CreateEvent(
            CatalogScanTraceKind.DirectoryExcluded,
            rootPath: @"C:\Root",
            fullPath: @"C:\Root\name") with
        {
            MatchInput = "name",
            MatchedRuleId = "skip-name",
            TraversalDecision = DirectoryTraversalDecision.SkipDescendants,
        };

        var line = ScanConsoleViewModel.CreateLine(trace);

        Assert.Equal(ScanConsoleLineTone.Pruned, line.Tone);
        Assert.Equal(@"C:\Root\name", line.Path);
        Assert.Contains("[EXCLUDED] [PRUNE]", line.Text, StringComparison.Ordinal);
        Assert.Contains("name=\"name\"", line.Text, StringComparison.Ordinal);
        Assert.Contains("rule=skip-name", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_exclusion_issue_is_rendered_as_a_warning_and_keeps_the_path()
    {
        var trace = CreateEvent(
            CatalogScanTraceKind.DirectoryExclusionIssue,
            rootPath: @"C:\Root",
            fullPath: @"C:\Root\slow") with
        {
            MatchInput = "slow",
            Issues =
            [
                new ProfileMappingIssue(
                    "directory_exclusion_regex_timeout",
                    null,
                    "제한 시간을 초과했습니다."),
            ],
            TraversalDecision = DirectoryTraversalDecision.Continue,
        };

        var line = ScanConsoleViewModel.CreateLine(trace);

        Assert.Equal(ScanConsoleLineTone.Warning, line.Tone);
        Assert.Equal(@"C:\Root\slow", line.Path);
        Assert.Contains("[EXCLUDE WARNING] [CONTINUE]", line.Text, StringComparison.Ordinal);
        Assert.Contains("directory_exclusion_regex_timeout", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_line_commands_copy_the_full_message_or_only_its_path()
    {
        List<string> clipboardWrites = [];
        var viewModel = new ScanConsoleViewModel(clipboardWrites.Add)
        {
            SelectedLine = ScanConsoleViewModel.CreateLine(CreateEvent(
                CatalogScanTraceKind.DirectoryVisited,
                rootPath: @"C:\Root",
                fullPath: @"C:\Root\2026\0521_Project")),
        };

        Assert.True(viewModel.CopySelectedMessageCommand.CanExecute(null));
        Assert.True(viewModel.CopySelectedPathCommand.CanExecute(null));

        viewModel.CopySelectedMessageCommand.Execute(null);
        viewModel.CopySelectedPathCommand.Execute(null);

        Assert.Equal(2, clipboardWrites.Count);
        Assert.Equal(
            Assert.IsType<ScanConsoleLineViewModel>(viewModel.SelectedLine).Text,
            clipboardWrites[0]);
        Assert.Equal(@"C:\Root\2026\0521_Project", clipboardWrites[1]);
    }

    [Fact]
    public void Path_copy_is_disabled_for_a_line_without_a_path()
    {
        var viewModel = new ScanConsoleViewModel(static _ => { })
        {
            SelectedLine = ScanConsoleViewModel.CreateLine(CreateEvent(
                CatalogScanTraceKind.Completed,
                rootPath: @"C:\Root")),
        };

        Assert.True(viewModel.CopySelectedMessageCommand.CanExecute(null));
        Assert.False(viewModel.CopySelectedPathCommand.CanExecute(null));
    }

    [Fact]
    public void Clipboard_failure_does_not_escape_the_copy_commands()
    {
        var feedback = new List<(string Title, string Message, ControlAppearance Appearance)>();
        var viewModel = new ScanConsoleViewModel(
            static _ => throw new InvalidOperationException("Clipboard is busy."),
            static _ => null, static (_, _, _) => Task.CompletedTask,
            (title, message, appearance) => feedback.Add((title, message, appearance)))
        {
            SelectedLine = ScanConsoleViewModel.CreateLine(CreateEvent(
                CatalogScanTraceKind.DirectoryVisited,
                rootPath: @"C:\Root",
                fullPath: @"C:\Root\Project")),
        };

        var messageException = Record.Exception(
            () => viewModel.CopySelectedMessageCommand.Execute(null));
        var pathException = Record.Exception(
            () => viewModel.CopySelectedPathCommand.Execute(null));

        Assert.Null(messageException);
        Assert.Null(pathException);
        Assert.Equal(2, feedback.Count);
        Assert.All(feedback, message =>
        {
            Assert.Equal("로그를 복사하지 못했습니다.", message.Title);
            Assert.Equal("Clipboard is busy.", message.Message);
            Assert.Equal(ControlAppearance.Secondary, message.Appearance);
        });
    }

    [Fact]
    public async Task Save_log_writes_the_click_time_snapshot_and_reports_success()
    {
        var writeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? suggestedFileName = null;
        string? writtenPath = null;
        IReadOnlyList<string>? writtenLines = null;
        var feedback = new List<(string Title, string Message, ControlAppearance Appearance)>();
        var viewModel = new ScanConsoleViewModel(
            static _ => { },
            suggested =>
            {
                suggestedFileName = suggested;
                return @"C:\Logs\scan.log";
            },
            async (path, lines, cancellationToken) =>
            {
                writtenPath = path;
                writtenLines = lines;
                writeStarted.TrySetResult();
                await releaseWrite.Task.WaitAsync(cancellationToken);
            },
            (title, message, appearance) => feedback.Add((title, message, appearance)));
        viewModel.Lines.Add(CreateDisplayLine("첫 번째 로그"));
        viewModel.Lines.Add(CreateDisplayLine("second log"));

        var operation = viewModel.SaveLogCommand.ExecuteAsync(null);
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.Lines.Add(CreateDisplayLine("저장 시작 후 추가된 로그"));
        releaseWrite.TrySetResult();
        await operation;

        Assert.NotNull(suggestedFileName);
        Assert.StartsWith("FindEverything-scan-", suggestedFileName, StringComparison.Ordinal);
        Assert.EndsWith(".log", suggestedFileName, StringComparison.Ordinal);
        Assert.Equal(@"C:\Logs\scan.log", writtenPath);
        Assert.Equal(["첫 번째 로그", "second log"], writtenLines);
        var success = Assert.Single(feedback);
        Assert.Equal("탐색 로그 저장 완료", success.Title);
        Assert.Contains("2줄", success.Message, StringComparison.Ordinal);
        Assert.Equal(ControlAppearance.Success, success.Appearance);
        Assert.False(viewModel.IsSavingLog);
        Assert.True(viewModel.SaveLogCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_log_does_nothing_when_the_file_picker_is_cancelled()
    {
        var writerCalled = false;
        var feedbackCalled = false;
        var viewModel = new ScanConsoleViewModel(
            static _ => { },
            static _ => null,
            (_, _, _) =>
            {
                writerCalled = true;
                return Task.CompletedTask;
            },
            (_, _, _) => feedbackCalled = true);
        viewModel.Lines.Add(CreateDisplayLine("로그"));

        await viewModel.SaveLogCommand.ExecuteAsync(null);

        Assert.False(writerCalled);
        Assert.False(feedbackCalled);
        Assert.False(viewModel.IsSavingLog);
        Assert.True(viewModel.SaveLogCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_log_failure_is_reported_without_escaping_the_command()
    {
        Exception? loggedException = null;
        var feedback = new List<(string Title, string Message, ControlAppearance Appearance)>();
        var viewModel = new ScanConsoleViewModel(
            static _ => { },
            static _ => @"C:\Logs\scan.log",
            static (_, _, _) => throw new IOException("디스크가 가득 찼습니다."),
            (title, message, appearance) => feedback.Add((title, message, appearance)),
            exception => loggedException = exception);
        viewModel.Lines.Add(CreateDisplayLine("로그"));

        var exception = await Record.ExceptionAsync(
            () => viewModel.SaveLogCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.IsType<IOException>(loggedException);
        var failure = Assert.Single(feedback);
        Assert.Equal("탐색 로그 저장 실패", failure.Title);
        Assert.Contains("디스크", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ControlAppearance.Danger, failure.Appearance);
        Assert.False(viewModel.IsSavingLog);
        Assert.True(viewModel.SaveLogCommand.CanExecute(null));
    }

    private static ScanConsoleLineViewModel CreateDisplayLine(string text) =>
        new(
            Guid.NewGuid(),
            1,
            CatalogScanTraceKind.DirectoryVisited,
            ScanConsoleLineTone.Match,
            text,
            string.Empty,
            null);

    private static CatalogScanTraceEvent CreateEvent(
        CatalogScanTraceKind kind,
        string rootPath,
        string? fullPath = null) =>
        new(
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            kind,
            "test-profile",
            "테스트 프로필",
            rootPath,
            fullPath,
            null,
            fullPath,
            kind == CatalogScanTraceKind.DirectoryVisited
                ? ProfileMapStatus.Success
                : null,
            null,
            new Dictionary<string, object?>(),
            [],
            kind == CatalogScanTraceKind.DirectoryVisited
                ? DirectoryTraversalDecision.Continue
                : null,
            "테스트 메시지");
}
