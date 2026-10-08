using System.IO;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ScanConsoleViewModelTests
{
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
        var viewModel = new ScanConsoleViewModel(
            static _ => throw new InvalidOperationException("Clipboard is busy."))
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
