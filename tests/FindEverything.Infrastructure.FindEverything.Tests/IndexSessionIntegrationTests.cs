using FindEverything.Application.Indexing;
using FindEverything.Infrastructure.FindEverything;
using Xunit;

namespace FindEverything.Infrastructure.FindEverything.Tests;

public sealed class IndexSessionIntegrationTests
{
    [Fact]
    public async Task ScanAndSearch_ReturnsEveryDirectoryAcrossPagesAndMapsProgress()
    {
        using var workspace = new TestWorkspace();
        var expectedPaths = new HashSet<string>(PathComparer);
        for (var index = 0; index < 1005; index++)
        {
            var path = Path.Combine(workspace.SourcePath, $"folder-{index:D4}");
            Directory.CreateDirectory(path);
            expectedPaths.Add(Path.GetFullPath(path));
        }

        await File.WriteAllTextAsync(Path.Combine(workspace.SourcePath, "not-a-directory.txt"), "content");

        var factory = new FindEverythingIndexSessionFactory();
        var progress = new RecordingProgress();
        IndexScanReport report;
        await using (var scanSession = factory.Create(workspace.DatabasePath))
        {
            report = await scanSession.ScanAsync(
                FastScan(workspace.SourcePath),
                progress,
                CancellationToken.None);
        }

        Assert.Equal(IndexScanStatus.Completed, report.Status);
        var lastProgress = Assert.IsType<IndexScanProgress>(progress.Last);
        Assert.Equal(report.Progress.Entries, lastProgress.Entries);
        Assert.Equal(report.Progress.Directories, lastProgress.Directories);
        Assert.Equal(report.Progress.ExcludedEntries, lastProgress.ExcludedEntries);
        Assert.Equal(report.Progress.SkippedLinks, lastProgress.SkippedLinks);
        Assert.Equal(report.Progress.ErrorCount, lastProgress.ErrorCount);
        Assert.Equal(report.Progress.PendingDirectories, lastProgress.PendingDirectories);

        DirectorySearchResult result;
        await using (var searchSession = factory.Create(workspace.DatabasePath))
        {
            result = await searchSession.SearchDirectoriesAsync(
                new DirectorySearchRequest(workspace.SourcePath) { PageSize = 128 },
                CancellationToken.None);
        }

        Assert.False(result.HasPendingScopes);
        Assert.Equal(expectedPaths.Count, result.Directories.Count);
        Assert.True(expectedPaths.SetEquals(result.Directories.Select(directory => directory.FullPath)));
    }

    [Fact]
    public async Task SearchWithoutAnIndex_DoesNotCreateADatabase()
    {
        using var workspace = new TestWorkspace();
        var factory = new FindEverythingIndexSessionFactory();

        await using var session = factory.Create(workspace.DatabasePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.SearchDirectoriesAsync(
            new DirectorySearchRequest(workspace.SourcePath),
            CancellationToken.None));

        Assert.False(File.Exists(workspace.DatabasePath));
    }

    [Fact]
    public async Task SearchWaitingForAnActiveScan_CanBeCancelled()
    {
        using var readableWorkspace = new TestWorkspace();
        using var slowWorkspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(readableWorkspace.SourcePath, "ready"));
        for (var index = 0; index < 4; index++)
            await File.WriteAllTextAsync(Path.Combine(slowWorkspace.SourcePath, $"slow-{index}.txt"), "content");

        var factory = new FindEverythingIndexSessionFactory();
        await using (var prepareSession = factory.Create(readableWorkspace.DatabasePath))
        {
            var prepared = await prepareSession.ScanAsync(
                FastScan(readableWorkspace.SourcePath),
                cancellationToken: CancellationToken.None);
            Assert.Equal(IndexScanStatus.Completed, prepared.Status);
        }

        await using var slowSession = factory.Create(slowWorkspace.DatabasePath);
        await using var searchSession = factory.Create(readableWorkspace.DatabasePath);
        using var slowCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowProgress = new RecordingProgress(_ => scanStarted.TrySetResult());
        var slowScan = slowSession.ScanAsync(new IndexScanRequest(slowWorkspace.SourcePath)
        {
            Options = new IndexScanOptions
            {
                DirectoryDelay = TimeSpan.Zero,
                MaxEntriesPerSecond = 1
            }
        }, slowProgress, slowCancellation.Token);

        try
        {
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var waitingCancellation = new CancellationTokenSource();
            waitingCancellation.CancelAfter(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searchSession.SearchDirectoriesAsync(
                new DirectorySearchRequest(readableWorkspace.SourcePath),
                waitingCancellation.Token));
        }
        finally
        {
            slowCancellation.Cancel();
        }

        var cancelled = await slowScan;
        Assert.Equal(IndexScanStatus.Cancelled, cancelled.Status);
    }

    private static IndexScanRequest FastScan(string sourcePath) => new(sourcePath)
    {
        Options = new IndexScanOptions
        {
            BatchSize = 512,
            DirectoryDelay = TimeSpan.Zero,
            MaxEntriesPerSecond = 1_000_000
        }
    };

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class RecordingProgress(Action<IndexScanProgress>? onReport = null) : IProgress<IndexScanProgress>
    {
        private readonly object _sync = new();
        private IndexScanProgress? _last;

        public IndexScanProgress? Last
        {
            get
            {
                lock (_sync)
                    return _last;
            }
        }

        public void Report(IndexScanProgress value)
        {
            lock (_sync)
                _last = value;
            onReport?.Invoke(value);
        }
    }
}
