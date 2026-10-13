using System.Runtime.CompilerServices;
using System.IO;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ImportedIndexBoundaryTests
{
    [Fact]
    public async Task ImportedFactoryBlocksWritesAtServiceBoundaryButKeepsOfflineReadsAndDisposal()
    {
        var local = Path.Combine(Path.GetTempPath(), "fe-boundary-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(local, Path.Combine(local, "settings.json"));
        var underlying = new FakeSessionFactory();
        var factory = new ImportedIndexSessionFactory(new(underlying), paths);
        var database = Path.Combine(local, "Imports", Guid.NewGuid().ToString("N"), "linked-0", "metadata.db");
        await using (var session = factory.Create(database))
        {
            Assert.Equal(database, session.DatabasePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ScanAsync(new(Path.Combine(local, "offline"))));
            await session.GetRootStatusAsync("offline");
            await session.SearchEntriesAsync(new("offline"));
            await session.SearchDirectoriesAsync(new("offline"));
            Assert.Equal(0, underlying.Last!.Writes);
            Assert.Equal(3, underlying.Last.Reads);
        }
        Assert.True(underlying.Last!.Disposed);
        await using var live = factory.Create(Path.Combine(local, "Imports-other", "metadata.db"));
        await live.ScanAsync(new(Path.Combine(local, "live")));
        Assert.Equal(1, underlying.Last!.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileQueryWrapperUsesStableOriginalSelectionsAndReturnsEffectiveOutputPaths(bool allMatching)
    {
        var root = Path.Combine(Path.GetTempPath(), "fe-key-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "producer");
        var linked = Path.Combine(root, "receiver");
        var source = new ImportedWorkspaceSource("source", "profile", "Shared", original, linked, root,
            Path.Combine(root, "metadata.db"), null, null, 2, false, false, DateTimeOffset.UtcNow, 7,
            new ProfileManifest { Kind = ProfileKind.Declarative });
        var inner = new FakeFileQuery([Entry(Path.Combine(linked, "a.txt")), Entry(Path.Combine(linked, "b.txt"))]);
        await using var query = new ImportedFileQuerySession(inner, source);
        Assert.Contains(":7:", query.Identity, StringComparison.Ordinal);
        Assert.Equal(2, await query.CountAsync());
        var page = await query.ReadRangeAsync(0, 2);
        Assert.Equal(Path.Combine(original, "a.txt"), page.Items[0].OriginalFullPath);
        Assert.Equal(Path.Combine(original, "a.txt").ToUpperInvariant(), page.Items[0].StableKey);
        Assert.Equal(Path.Combine(linked, "a.txt"), page.Items[0].FullPath);
        var rows = new List<IndexedPathEntry>();
        await foreach (var row in query.EnumerateSelectionAsync(new(allMatching, [Path.Combine(original, "a.txt").ToUpperInvariant()]))) rows.Add(row);
        Assert.Equal(Path.Combine(linked, allMatching ? "b.txt" : "a.txt"), Assert.Single(rows).FullPath);
        Assert.Equal(Path.Combine(linked, "a.txt"), Assert.Single(inner.Captured!.Keys), ignoreCase: true);
        Assert.Equal(allMatching, inner.Captured.AllMatching);
    }

    private static IndexedPathEntry Entry(string path) => new(path, Path.GetFileName(path), Path.GetDirectoryName(path)!,
        IndexedPathKind.File, 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);
    private sealed class FakeFileQuery(IReadOnlyList<IndexedPathEntry> rows) : IResultQuerySession<IndexedPathEntry>
    {
        public QuerySelection? Captured { get; private set; }
        public string Identity => "generation";
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)rows.Count);
        public Task<ResultPage<IndexedPathEntry>> ReadRangeAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultPage<IndexedPathEntry>(rows.Skip(offset).Take(limit).ToArray(), offset, offset + limit < rows.Count));
        public async IAsyncEnumerable<IndexedPathEntry> EnumerateSelectionAsync(QuerySelection selection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Captured = selection.Capture();
            await Task.CompletedTask;
            foreach (var row in rows) if (selection.Contains(row.FullPath)) yield return row;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeSessionFactory : IIndexSessionFactory
    {
        public FakeSession? Last { get; private set; }
        public IIndexSession Create(string databasePath) => Last = new(databasePath);
    }
    private sealed class FakeSession(string path) : IIndexSession
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public string DatabasePath => path;
        public Task<IndexScanReport> ScanAsync(IndexScanRequest request, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default)
        { Writes++; return Task.FromResult(new IndexScanReport(Guid.NewGuid(), request.RootPath, request.RootPath, IndexScanStatus.Completed,
            new(0, 0, 0, 0, 0, TimeSpan.Zero, 0), [], [])); }
        public Task<DirectorySearchResult> SearchDirectoriesAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(new DirectorySearchResult([], false)); }
        public Task<EntrySearchResult> SearchEntriesAsync(EntrySearchRequest request, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(new EntrySearchResult([], 0, false, false)); }
        public Task<IndexRootStatus> GetRootStatusAsync(string rootPath, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(new IndexRootStatus(rootPath, IndexRootAvailability.Available, Guid.NewGuid(), rootPath,
            IndexScanStatus.Completed, DateTimeOffset.UtcNow, 0, 0, false)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
