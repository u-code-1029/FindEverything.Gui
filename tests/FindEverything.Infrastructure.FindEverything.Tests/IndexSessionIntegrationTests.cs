using FindEverything.Application.Indexing;
using FindEverything.Infrastructure.FindEverything;
using Microsoft.Data.Sqlite;
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
        var reportedPages = new List<IReadOnlyList<IndexedDirectory>>();
        await using (var searchSession = factory.Create(workspace.DatabasePath))
        {
            result = await searchSession.SearchDirectoriesAsync(
                new DirectorySearchRequest(workspace.SourcePath)
                {
                    PageSize = 128,
                    PageProgress = new InlineProgress<IReadOnlyList<IndexedDirectory>>(
                        page => reportedPages.Add(page)),
                },
                CancellationToken.None);
        }

        Assert.False(result.HasPendingScopes);
        Assert.Equal(expectedPaths.Count, result.Directories.Count);
        Assert.True(expectedPaths.SetEquals(result.Directories.Select(directory => directory.FullPath)));
        Assert.True(reportedPages.Count > 1);
        Assert.All(reportedPages, page => Assert.InRange(page.Count, 1, 128));
        Assert.Equal(
            result.Directories.Select(static directory => directory.FullPath),
            reportedPages.SelectMany(static page => page).Select(static directory => directory.FullPath));
    }

    [Fact]
    public async Task SearchDirectories_CanStreamPagesWithoutRetainingASecondCopy()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 7; index++)
            Directory.CreateDirectory(Path.Combine(workspace.SourcePath, $"folder-{index}"));

        var factory = new FindEverythingIndexSessionFactory();
        await using (var scanSession = factory.Create(workspace.DatabasePath))
        {
            await scanSession.ScanAsync(
                FastScan(workspace.SourcePath),
                cancellationToken: CancellationToken.None);
        }

        var streamed = new List<IndexedDirectory>();
        var consumerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var searchSession = factory.Create(workspace.DatabasePath);
        var search = searchSession.SearchDirectoriesAsync(
            new DirectorySearchRequest(workspace.SourcePath)
            {
                PageSize = 3,
                IncludeDirectoriesInResult = false,
                PageConsumer = async (page, cancellationToken) =>
                {
                    consumerStarted.TrySetResult();
                    await releaseConsumer.Task.WaitAsync(cancellationToken);
                    streamed.AddRange(page);
                },
            },
            CancellationToken.None);
        await consumerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(search.IsCompleted);
        releaseConsumer.SetResult();
        var result = await search;

        Assert.Empty(result.Directories);
        Assert.Equal(7, streamed.Count);
        Assert.Equal(7, streamed.Select(static item => item.FullPath).Distinct(PathComparer).Count());
    }

    [Fact]
    public async Task SearchDirectories_RequiresAwaitedConsumerWhenResultDoesNotRetainDirectories()
    {
        using var workspace = new TestWorkspace();
        var factory = new FindEverythingIndexSessionFactory();
        await using var session = factory.Create(workspace.DatabasePath);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            session.SearchDirectoriesAsync(
                new DirectorySearchRequest(workspace.SourcePath)
                {
                    IncludeDirectoriesInResult = false,
                    PageProgress = new InlineProgress<IReadOnlyList<IndexedDirectory>>(static _ => { }),
                },
                CancellationToken.None));

        Assert.Equal("request", exception.ParamName);
        Assert.Contains("page consumer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(workspace.DatabasePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchDirectories_RejectsResultWhenPublishedSnapshotChanges(
        bool changeGeneration)
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 3; index++)
            Directory.CreateDirectory(Path.Combine(workspace.SourcePath, $"folder-{index}"));

        var factory = new FindEverythingIndexSessionFactory();
        IndexScanReport report;
        await using (var scanSession = factory.Create(workspace.DatabasePath))
        {
            report = await scanSession.ScanAsync(
                FastScan(workspace.SourcePath),
                cancellationToken: CancellationToken.None);
        }
        Assert.Equal(IndexScanStatus.Completed, report.Status);

        var replacementScanId = Guid.NewGuid();
        var mutated = false;
        var consumerCalls = 0;
        await using var searchSession = factory.Create(workspace.DatabasePath);
        var exception = await Assert.ThrowsAsync<IndexSnapshotChangedException>(() =>
            searchSession.SearchDirectoriesAsync(
                new DirectorySearchRequest(workspace.SourcePath)
                {
                    PageSize = 1,
                    IncludeDirectoriesInResult = false,
                    PageConsumer = (_, _) =>
                    {
                        consumerCalls++;
                        if (!mutated)
                        {
                            using var connection = new SqliteConnection(
                                $"Data Source={workspace.DatabasePath}");
                            connection.Open();
                            using var command = connection.CreateCommand();
                            command.CommandText = changeGeneration
                                ? "UPDATE roots SET last_scan_id=$scan;"
                                : "UPDATE roots SET last_status=$status;";
                            if (changeGeneration)
                            {
                                command.Parameters.AddWithValue(
                                    "$scan",
                                    replacementScanId.ToString("N"));
                            }
                            else
                            {
                                command.Parameters.AddWithValue(
                                    "$status",
                                    (int)IndexScanStatus.Partial);
                            }
                            Assert.Equal(1, command.ExecuteNonQuery());
                            mutated = true;
                        }

                        return ValueTask.CompletedTask;
                    },
                },
                CancellationToken.None));

        Assert.True(mutated);
        Assert.Equal(3, consumerCalls);
        Assert.Equal(Path.GetFullPath(workspace.SourcePath), exception.RootPath);
        Assert.Equal(report.ScanId, exception.ExpectedScanId);
        Assert.Equal(
            changeGeneration ? replacementScanId : report.ScanId,
            exception.ActualScanId);
    }

    [Fact]
    public async Task ScanMapsDirectoryInspectionDecisionsAndPendingCoverageThroughTheAdapter()
    {
        using var workspace = new TestWorkspace();
        var terminalPath = Path.Combine(workspace.SourcePath, "terminal");
        var hiddenPath = Path.Combine(terminalPath, "deep");
        var deferredPath = Path.Combine(workspace.SourcePath, "deferred");
        var excludedPath = Path.Combine(workspace.SourcePath, "deferred-excluded");
        var excludedDescendantPath = Path.Combine(excludedPath, "deep");
        Directory.CreateDirectory(hiddenPath);
        Directory.CreateDirectory(Path.Combine(deferredPath, "deep"));
        Directory.CreateDirectory(excludedDescendantPath);
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, "sibling"));
        var candidates = new List<DiscoveredDirectory>();
        var factory = new FindEverythingIndexSessionFactory();
        var request = FastScan(workspace.SourcePath) with
        {
            Options = FastScan(workspace.SourcePath).Options with
            {
                Deferral = new IndexDeferralPolicy
                {
                    DirectoryNames = ["deferred", "deferred-excluded"],
                },
            },
            InspectDirectory = candidate =>
            {
                candidates.Add(candidate);
                if (PathComparer.Equals(candidate.FullPath, terminalPath))
                    return DirectoryTraversalDecision.SkipDescendants;
                return PathComparer.Equals(candidate.FullPath, excludedPath)
                    ? DirectoryTraversalDecision.ExcludeSubtree
                    : DirectoryTraversalDecision.Continue;
            },
        };

        await using var session = factory.Create(workspace.DatabasePath);
        var report = await session.ScanAsync(request, cancellationToken: CancellationToken.None);
        var indexed = await session.SearchDirectoriesAsync(
            new DirectorySearchRequest(workspace.SourcePath),
            CancellationToken.None);

        Assert.Equal(IndexScanStatus.Deferred, report.Status);
        Assert.Contains(candidates, candidate =>
            PathComparer.Equals(candidate.FullPath, workspace.SourcePath)
            && !candidate.CoveragePending);
        Assert.Contains(candidates, candidate =>
            PathComparer.Equals(candidate.FullPath, terminalPath)
            && !candidate.CoveragePending);
        Assert.Contains(candidates, candidate =>
            PathComparer.Equals(candidate.FullPath, deferredPath)
            && candidate.CoveragePending);
        Assert.Contains(candidates, candidate =>
            PathComparer.Equals(candidate.FullPath, excludedPath)
            && candidate.CoveragePending);
        Assert.DoesNotContain(candidates, candidate =>
            PathComparer.Equals(candidate.FullPath, hiddenPath)
            || PathComparer.Equals(candidate.FullPath, excludedDescendantPath));
        Assert.Contains(indexed.Directories, directory =>
            PathComparer.Equals(directory.FullPath, terminalPath));
        Assert.Contains(indexed.Directories, directory =>
            PathComparer.Equals(directory.FullPath, deferredPath)
            && directory.CoveragePending);
        Assert.DoesNotContain(indexed.Directories, directory =>
            PathComparer.Equals(directory.FullPath, hiddenPath)
            || PathComparer.Equals(directory.FullPath, excludedPath)
            || PathComparer.Equals(directory.FullPath, excludedDescendantPath));
        Assert.DoesNotContain(report.PendingScopes, scope =>
            PathComparer.Equals(scope.ScopePath, excludedPath));
        Assert.True(indexed.HasPendingScopes);
    }

    [Fact]
    public async Task ScanUsesTheConfiguredDirectoryRegexTimeoutAndContinuesTraversal()
    {
        using var workspace = new TestWorkspace();
        var pathologicalPath = Path.Combine(
            workspace.SourcePath,
            new string('a', 96) + "!");
        var retainedPath = Path.Combine(workspace.SourcePath, "retained");
        Directory.CreateDirectory(pathologicalPath);
        Directory.CreateDirectory(retainedPath);
        var baseRequest = FastScan(workspace.SourcePath);
        var request = baseRequest with
        {
            Options = baseRequest.Options with
            {
                ExcludedDirectoryNameRegexes =
                [
                    new IndexDirectoryNameRegex("(a+)+$")
                    {
                        TimeoutMilliseconds = 1,
                    },
                ],
            },
        };

        var factory = new FindEverythingIndexSessionFactory();
        await using var session = factory.Create(workspace.DatabasePath);
        var report = await session.ScanAsync(request, cancellationToken: CancellationToken.None);
        var indexed = await session.SearchDirectoriesAsync(
            new DirectorySearchRequest(workspace.SourcePath),
            CancellationToken.None);

        Assert.Equal(IndexScanStatus.Partial, report.Status);
        var error = Assert.Single(report.Errors);
        Assert.Equal(pathologicalPath, error.Path);
        Assert.Contains("1 ms timeout", error.Message, StringComparison.Ordinal);
        Assert.Contains(indexed.Directories, directory =>
            PathComparer.Equals(directory.FullPath, pathologicalPath));
        Assert.Contains(indexed.Directories, directory =>
            PathComparer.Equals(directory.FullPath, retainedPath));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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

        await Assert.ThrowsAsync<FileNotFoundException>(() => session.SearchEntriesAsync(
            new EntrySearchRequest(workspace.SourcePath),
            CancellationToken.None));

        var status = await session.GetRootStatusAsync(workspace.SourcePath, CancellationToken.None);
        Assert.Equal(IndexRootAvailability.DatabaseMissing, status.Availability);
        Assert.Equal(0, status.EntryCount);
        Assert.Null(status.LastPublishedUtc);

        Assert.False(File.Exists(workspace.DatabasePath));
    }

    [Fact]
    public async Task SearchEntries_MapsFiltersSortPagingAndMetadata()
    {
        using var workspace = new TestWorkspace();
        var firstPath = Path.Combine(workspace.SourcePath, "alpha-report-old.txt");
        var secondPath = Path.Combine(workspace.SourcePath, "alpha-report-new.txt");
        var excludedBySizePath = Path.Combine(workspace.SourcePath, "alpha-report-large.txt");
        await File.WriteAllTextAsync(firstPath, "1234");
        await File.WriteAllTextAsync(secondPath, "12345678");
        await File.WriteAllTextAsync(excludedBySizePath, new string('x', 32));
        var oldTime = DateTime.UtcNow.AddDays(-2);
        var newTime = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(firstPath, oldTime);
        File.SetLastWriteTimeUtc(secondPath, newTime);
        var recordedOldTime = File.GetLastWriteTimeUtc(firstPath);
        var recordedNewTime = File.GetLastWriteTimeUtc(secondPath);

        var factory = new FindEverythingIndexSessionFactory();
        await using (var scanSession = factory.Create(workspace.DatabasePath))
        {
            await scanSession.ScanAsync(
                FastScan(workspace.SourcePath),
                cancellationToken: CancellationToken.None);
        }

        await using var session = factory.Create(workspace.DatabasePath);
        var result = await session.SearchEntriesAsync(new EntrySearchRequest(workspace.SourcePath)
        {
            Keyword = "alpha report",
            Kind = IndexedPathKind.File,
            MinSizeBytes = 4,
            MaxSizeBytes = 8,
            ModifiedFromUtc = new DateTimeOffset(recordedOldTime.AddHours(-1), TimeSpan.Zero),
            ModifiedBeforeUtc = new DateTimeOffset(recordedNewTime.AddHours(1), TimeSpan.Zero),
            SortBy = EntrySortField.Modified,
            SortDirection = EntrySortDirection.Descending,
            Limit = 1
        }, CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(Path.GetFullPath(secondPath), entry.FullPath);
        Assert.Equal("alpha-report-new.txt", entry.Name);
        Assert.Equal(Path.GetFullPath(workspace.SourcePath), entry.ParentPath);
        Assert.Equal(IndexedPathKind.File, entry.Kind);
        Assert.Equal(8, entry.SizeBytes);
        Assert.Equal(new DateTimeOffset(recordedNewTime, TimeSpan.Zero), entry.ModifiedUtc);
        Assert.False(entry.CoveragePending);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.HasMore);
        Assert.False(result.HasPendingScopes);

        var secondPage = await session.SearchEntriesAsync(new EntrySearchRequest(workspace.SourcePath)
        {
            Keyword = "alpha report",
            Kind = IndexedPathKind.File,
            MinSizeBytes = 4,
            MaxSizeBytes = 8,
            SortBy = EntrySortField.Modified,
            SortDirection = EntrySortDirection.Descending,
            Limit = 1,
            Offset = 1
        }, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(firstPath), Assert.Single(secondPage.Entries).FullPath);
        Assert.Equal(2, secondPage.TotalCount);
        Assert.False(secondPage.HasMore);
    }

    [Fact]
    public async Task OnDemandScopedRefresh_ReplacesOnlyTheSelectedSubtree()
    {
        using var workspace = new TestWorkspace();
        var selectedScope = Directory.CreateDirectory(
            Path.Combine(workspace.SourcePath, "selected")).FullName;
        var siblingScope = Directory.CreateDirectory(
            Path.Combine(workspace.SourcePath, "sibling")).FullName;
        var staleSelectedPath = Path.Combine(selectedScope, "stale.txt");
        var retainedSiblingPath = Path.Combine(siblingScope, "retained.txt");
        await File.WriteAllTextAsync(staleSelectedPath, "stale");
        await File.WriteAllTextAsync(retainedSiblingPath, "retained");

        var factory = new FindEverythingIndexSessionFactory();
        await using var session = factory.Create(workspace.DatabasePath);
        var initial = await session.ScanAsync(
            FastScan(workspace.SourcePath),
            cancellationToken: CancellationToken.None);
        Assert.Equal(IndexScanStatus.Completed, initial.Status);

        File.Delete(staleSelectedPath);
        var addedSelectedPath = Path.Combine(selectedScope, "added.txt");
        var unseenSiblingPath = Path.Combine(siblingScope, "not-yet-indexed.txt");
        await File.WriteAllTextAsync(addedSelectedPath, "added");
        await File.WriteAllTextAsync(unseenSiblingPath, "outside selected scope");

        var scopedRequest = FastScan(workspace.SourcePath) with
        {
            ScopePath = selectedScope,
            OnDemand = true,
        };
        var scoped = await session.ScanAsync(
            scopedRequest,
            cancellationToken: CancellationToken.None);
        var result = await session.SearchEntriesAsync(
            new EntrySearchRequest(workspace.SourcePath)
            {
                SortBy = EntrySortField.Path,
                Limit = 1000,
            },
            CancellationToken.None);
        var rootStatus = await session.GetRootStatusAsync(
            workspace.SourcePath,
            CancellationToken.None);
        var indexedPaths = result.Entries
            .Select(static entry => entry.FullPath)
            .ToHashSet(PathComparer);

        Assert.Equal(IndexScanStatus.Completed, scoped.Status);
        Assert.Equal(Path.GetFullPath(workspace.SourcePath), scoped.RootPath);
        Assert.Equal(Path.GetFullPath(selectedScope), scoped.ScopePath);
        Assert.DoesNotContain(Path.GetFullPath(staleSelectedPath), indexedPaths);
        Assert.Contains(Path.GetFullPath(addedSelectedPath), indexedPaths);
        Assert.Contains(Path.GetFullPath(retainedSiblingPath), indexedPaths);
        Assert.DoesNotContain(Path.GetFullPath(unseenSiblingPath), indexedPaths);
        Assert.Equal(IndexRootAvailability.Available, rootStatus.Availability);
        Assert.Equal(result.TotalCount, rootStatus.EntryCount);
    }

    [Fact]
    public async Task RootStatus_DistinguishesIndexedAndUnregisteredRootsInSharedDatabase()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.SourcePath, "ready.txt"), "content");
        var unregisteredRoot = Path.Combine(workspace.SourcePath, "not-indexed-separately");
        Directory.CreateDirectory(unregisteredRoot);
        var factory = new FindEverythingIndexSessionFactory();
        await using var session = factory.Create(workspace.DatabasePath);
        var report = await session.ScanAsync(
            FastScan(workspace.SourcePath),
            cancellationToken: CancellationToken.None);

        var indexed = await session.GetRootStatusAsync(workspace.SourcePath, CancellationToken.None);
        var unregistered = await session.GetRootStatusAsync(unregisteredRoot, CancellationToken.None);

        Assert.Equal(IndexRootAvailability.Available, indexed.Availability);
        Assert.Equal(report.ScanId, indexed.LastScanId);
        Assert.Equal(IndexScanStatus.Completed, indexed.LastStatus);
        Assert.NotNull(indexed.LastPublishedUtc);
        Assert.True(indexed.EntryCount >= 2);
        Assert.Equal(0, indexed.LastErrorCount);
        Assert.False(indexed.HasPendingScopes);
        Assert.Equal(IndexRootAvailability.RootNotIndexed, unregistered.Availability);
        Assert.Equal(0, unregistered.EntryCount);
    }

    [Fact]
    public async Task RootStatus_FirstCancelledScanIsNotReportedAsAvailable()
    {
        using var workspace = new TestWorkspace();
        for (var index = 0; index < 4; index++)
            await File.WriteAllTextAsync(Path.Combine(workspace.SourcePath, $"slow-{index}.txt"), "content");

        var factory = new FindEverythingIndexSessionFactory();
        await using var session = factory.Create(workspace.DatabasePath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new RecordingProgress(_ => scanStarted.TrySetResult());
        var scan = session.ScanAsync(new IndexScanRequest(workspace.SourcePath)
        {
            Options = new IndexScanOptions
            {
                DirectoryDelay = TimeSpan.Zero,
                MaxEntriesPerSecond = 1
            }
        }, progress, cancellation.Token);

        await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        var report = await scan;
        var status = await session.GetRootStatusAsync(workspace.SourcePath, CancellationToken.None);

        Assert.Equal(IndexScanStatus.Cancelled, report.Status);
        Assert.Equal(IndexRootAvailability.RootNotIndexed, status.Availability);
        Assert.Null(status.LastPublishedUtc);
        Assert.Equal(0, status.EntryCount);
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

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searchSession.SearchEntriesAsync(
                new EntrySearchRequest(readableWorkspace.SourcePath),
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
