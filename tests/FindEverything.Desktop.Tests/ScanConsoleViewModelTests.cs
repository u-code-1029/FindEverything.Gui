using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ScanConsoleViewModelTests
{
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
        var completed = ScanConsoleViewModel.CreateLine(CreateEvent(
            CatalogScanTraceKind.Completed,
            rootPath: @"Z:\Projects"));

        Assert.Equal(@"Z:\Projects", started.Path);
        Assert.True(started.HasPath);
        Assert.Equal(@"Z:\Projects\2026\0521_Project", visited.Path);
        Assert.Equal(@"Z:\Projects\Private", error.Path);
        Assert.Null(completed.Path);
        Assert.False(completed.HasPath);
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
