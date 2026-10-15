using System.Runtime.CompilerServices;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Infrastructure.Persistence;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class SqliteSelectedFileQueryTests
{
    [Fact]
    public async Task Five_thousand_rows_are_streamed_once_and_deep_pages_keep_order_and_metadata()
    {
        var source = new GeneratedQuery(5000);
        using var selection = new QuerySelection(true);
        selection.Set("stable-3", false);
        var session = await SqliteSelectedFileQuery.CreateAsync(source, selection);
        var path = Assert.IsType<SqliteSelectedFileQuery>(session).DatabasePath;
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(source.Identity, session.Identity);
            Assert.Equal(1, source.EnumerationCalls);
            Assert.True(source.EnumerationDisposed);
            Assert.Equal(0, source.DisposeCalls);
            selection.Clear(); // The completed review is an immutable snapshot.
            Assert.Equal(4999, await session.CountAsync());
            var deep = await session.ReadRangeAsync(4000, 500);
            Assert.Equal(4000, deep.Offset);
            Assert.Equal(500, deep.Items.Count);
            Assert.True(deep.HasMore);
            Assert.Equal(Entry(4001), deep.Items[0]);
            Assert.Equal(Entry(4500), deep.Items[^1]);
            var last = await session.ReadRangeAsync(4998, 500);
            Assert.Equal(Entry(4999), Assert.Single(last.Items));
            Assert.False(last.HasMore);
            Assert.Empty((await session.ReadRangeAsync(6000, 500)).Items);
            Assert.Equal(4999, await session.CountAsync());
            Assert.Equal(1, source.EnumerationCalls);
            Assert.Equal(0, source.CountCalls);
            Assert.Equal(0, source.RangeCalls);
        }
        finally { await session.DisposeAsync(); }
        Assert.Equal(1, source.DisposeCalls);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        await session.DisposeAsync();
        Assert.Equal(1, source.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadRangeAsync(0, 5));
    }

    [Fact]
    public async Task Selected_rows_and_key_only_counts_use_stable_keys_across_effective_path_mapping()
    {
        var source = new GeneratedQuery(5000);
        using var all = new QuerySelection(true);
        await using var session = await SqliteSelectedFileQuery.CreateAsync(source, all);
        using var explicitSelection = new QuerySelection(keys: ["STABLE-7", "stable-4001", "missing"]);
        Assert.Equal(2, await session.CountSelectionAsync(explicitSelection));
        var rows = new List<IndexedPathEntry>();
        await foreach (var row in session.EnumerateSelectionAsync(explicitSelection)) rows.Add(row);
        Assert.Equal(new[] { Entry(7), Entry(4001) }, rows);
        var keyReader = Assert.IsAssignableFrom<IResultSelectionKeyReader>(session);
        var keys = new List<ResultSelectionKey>();
        await foreach (var key in keyReader.EnumerateSelectionKeysAsync(explicitSelection)) keys.Add(key);
        Assert.Equal(new[] { new ResultSelectionKey("stable-7", Entry(7).FullPath),
            new ResultSelectionKey("stable-4001", Entry(4001).FullPath) }, keys);

        using var baseline = await SqliteSelectionSnapshot.CreateAsync(session, explicitSelection,
            row => row.StableKey!, row => row.FullPath);
        using var selected = new QuerySelection();
        selected.ReplaceBaseline(baseline);
        selected.Set("stable-7", false);
        selected.Set("stable-9", true);
        Assert.Equal(2, await session.CountSelectionAsync(selected));
        selected.RemoveUnavailableKey("stable-4001");
        Assert.Equal(1, await session.CountSelectionAsync(selected));
        selected.Invert();
        Assert.Equal(4998, await session.CountSelectionAsync(selected));
        Assert.Equal(1, source.EnumerationCalls);
        Assert.Equal(0, source.RangeCalls);
    }

    [Fact]
    public async Task Cancellation_cleans_partial_database_without_taking_source_ownership()
    {
        var before = TemporaryDirectories();
        using var cancellation = new CancellationTokenSource();
        using var all = new QuerySelection(true);
        var source = new GeneratedQuery(5000) { BlockAfterFirst = true };
        var building = SqliteSelectedFileQuery.CreateAsync(source, all, cancellation.Token);
        await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building);
        Assert.True(source.EnumerationDisposed);
        Assert.Equal(0, source.DisposeCalls);
        Assert.Empty(TemporaryDirectories().Except(before));
        await source.DisposeAsync();
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task Generation_failure_cleans_partial_database_and_leaves_the_source_with_its_caller()
    {
        var before = TemporaryDirectories();
        using var all = new QuerySelection(true);
        var source = new GeneratedQuery(5000) { FailAfterFirst = true };
        await Assert.ThrowsAsync<IndexSnapshotChangedException>(() => SqliteSelectedFileQuery.CreateAsync(source, all));
        Assert.True(source.EnumerationDisposed);
        Assert.Equal(0, source.DisposeCalls);
        Assert.Empty(TemporaryDirectories().Except(before));
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_before_start_never_enumerates_or_owns_the_source()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var all = new QuerySelection(true);
        var source = new GeneratedQuery(5000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqliteSelectedFileQuery.CreateAsync(source, all, cancellation.Token));
        Assert.Equal(0, source.EnumerationCalls);
        Assert.Equal(0, source.DisposeCalls);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Disposal_failure_still_removes_the_private_snapshot()
    {
        using var all = new QuerySelection(true);
        var source = new GeneratedQuery(2) { FailDisposal = true };
        var session = await SqliteSelectedFileQuery.CreateAsync(source, all);
        var path = Assert.IsType<SqliteSelectedFileQuery>(session).DatabasePath;
        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
        Assert.Equal(1, source.DisposeCalls);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    private static HashSet<string> TemporaryDirectories() => Directory
        .EnumerateDirectories(Path.GetTempPath(), "FindEverything-selected-files-*").ToHashSet(StringComparer.Ordinal);

    private static IndexedPathEntry Entry(int index) => new(
        $@"Z:\Mapped\item-{index:D5}.txt", $"item-{index:D5}.txt", @"Z:\Mapped",
        index % 3 == 0 ? IndexedPathKind.Directory : IndexedPathKind.File,
        index % 3 == 0 ? null : index * 1024L,
        new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero).AddMinutes(index),
        new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(index),
        index % 7 == 0)
    {
        StableKey = "stable-" + index,
        OriginalFullPath = $@"\\192.0.2.1\share\item-{index:D5}.txt",
    };

    private sealed class GeneratedQuery(int count) : IResultQuerySession<IndexedPathEntry>
    {
        public string Identity => "fixed-source-generation";
        public int EnumerationCalls { get; private set; }
        public int CountCalls { get; private set; }
        public int RangeCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool EnumerationDisposed { get; private set; }
        public bool BlockAfterFirst { get; init; }
        public bool FailAfterFirst { get; init; }
        public bool FailDisposal { get; init; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> CountAsync(CancellationToken cancellationToken = default)
        { CountCalls++; throw new InvalidOperationException("Building a review must not count its source."); }
        public Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default)
        { RangeCalls++; throw new InvalidOperationException("Review pages must not reread the source."); }
        public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnumerationCalls++;
            try
            {
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index == 1 && FailAfterFirst)
                        throw new IndexSnapshotChangedException(@"Z:\Mapped", Guid.NewGuid(), Guid.NewGuid());
                    if (index == 1 && BlockAfterFirst)
                    {
                        Blocked.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    var row = Entry(index);
                    if (selection.Contains(row.StableKey!)) yield return row;
                }
            }
            finally { EnumerationDisposed = true; }
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return FailDisposal ? ValueTask.FromException(new IOException("Source disposal failed.")) : ValueTask.CompletedTask;
        }
    }
}
