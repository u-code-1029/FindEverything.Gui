using System.Diagnostics.CodeAnalysis;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogEntryPathsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disjoint_roots_share_one_result_and_aggregate_reports_progress_and_log(bool discover)
    {
        var fixture = new Fixture();
        fixture.Children[fixture.A] = [Path.Combine(fixture.A, "one")];
        fixture.Children[fixture.B] = [Path.Combine(fixture.B, "two")];
        var updates = new List<CatalogOperationProgress>();
        var trace = new Trace();
        await using var provider = Provider(fixture, trace: trace);
        var service = provider.GetRequiredService<ICatalogService>();
        var request = Request(fixture, fixture.A, fixture.B);
        var result = discover
            ? await service.DiscoverAsync(request, new Progress(updates.Add))
            : await service.ScanAndLoadAsync(request, new Progress(updates.Add));

        Assert.Equal(4, result.CandidateCount);
        Assert.Equal(4, result.MatchedCount);
        Assert.Equal(4, result.Items.Select(static item => item.FullPath).Distinct().Count());
        Assert.Equal([fixture.A, fixture.B], fixture.VisitedRoots);
        Assert.Contains(updates, update => update.EntryIndex == 1 && update.EntryCount == 2 && update.EntryPath == fixture.A);
        Assert.Contains(updates, update => update.EntryIndex == 2 && update.MatchedCount == 4 && update.EntryPath == fixture.B);
        Assert.Single(trace.Events, static value => value.Kind == CatalogScanTraceKind.Started);
        Assert.Single(trace.Events, static value => value.Kind == CatalogScanTraceKind.Completed);
        Assert.Single(trace.Events.Select(static value => value.OperationId).Distinct());
        Assert.Equal(Enumerable.Range(1, trace.Events.Count).Select(static n => (long)n), trace.Events.Select(static value => value.Sequence));
        if (discover)
        {
            Assert.Equal(2, result.DiscoveryReports.Count);
            Assert.Equal(4, result.DiscoveryReport!.Progress.Directories);
            Assert.Equal(TimeSpan.FromSeconds(2), result.DiscoveryReport.Progress.Elapsed);
        }
        else
        {
            Assert.Equal(2, result.ScanReports.Count);
            Assert.Equal(4, result.ScanReport!.Progress.Entries);
            Assert.Equal(TimeSpan.FromSeconds(2), result.ScanReport.Progress.Elapsed);
            Assert.Contains(updates, update => update.ScanProgress is { Entries: 4 });
        }
    }

    [Fact]
    public async Task Overlapping_roots_are_not_collapsed_and_have_independent_depth_budgets()
    {
        var fixture = new Fixture { MaxDepth = 1 };
        var nested = Path.Combine(fixture.A, "nested");
        var child = Path.Combine(nested, "child");
        fixture.Children[fixture.A] = [nested];
        fixture.Children[nested] = [child];
        await using var provider = Provider(fixture);
        var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(Request(fixture, fixture.A, nested));

        Assert.Equal([fixture.A, nested], fixture.VisitedRoots);
        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(3, result.MatchedCount);
        Assert.Equal(3, fixture.MappedPaths.Count);
        Assert.Contains(result.Items, item => item.FullPath == child);
        Assert.Equal(DirectoryTraversalDecision.SkipDescendants, fixture.Decisions[(fixture.A, nested)]);
        Assert.Equal(DirectoryTraversalDecision.Continue, fixture.Decisions[(nested, nested)]);
    }

    [Fact]
    public async Task Excluded_descendant_can_still_be_an_explicit_entry_root()
    {
        var fixture = new Fixture { ExcludedName = "skip" };
        var nested = Path.Combine(fixture.A, "skip");
        fixture.Children[fixture.A] = [nested];
        await using var provider = Provider(fixture);
        var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(Request(fixture, fixture.A, nested));
        Assert.Equal(2, result.MatchedCount);
        Assert.Contains(result.Items, item => item.FullPath == nested);
        Assert.Equal(DirectoryTraversalDecision.ExcludeSubtree, fixture.Decisions[(fixture.A, nested)]);
        Assert.Equal(DirectoryTraversalDecision.Continue, fixture.Decisions[(nested, nested)]);
    }

    [Fact]
    public async Task Equivalent_canonical_entry_paths_are_scanned_only_once()
    {
        var fixture = new Fixture();
        var canonicalizer = new Canonicalizer(fixture.B, fixture.A);
        await using var provider = Provider(fixture, canonicalizer: canonicalizer);
        var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(
            Request(fixture, fixture.A, fixture.A + Path.DirectorySeparatorChar, fixture.B));
        Assert.Equal([fixture.A], fixture.VisitedRoots);
        Assert.Equal(1, result.MatchedCount);
        Assert.Single(result.ScanReports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_stops_remaining_roots_and_never_publishes_partial_rows(bool cancel)
    {
        var fixture = new Fixture { FailureRootIsB = true, CancelOnFailure = cancel };
        var writer = new Writer();
        await using var provider = Provider(fixture);
        var task = provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(
            Request(fixture, fixture.A, fixture.B, fixture.C) with { ResultWriter = writer, RetainItems = false });
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Equal([fixture.A, fixture.B], fixture.VisitedRoots);
        Assert.Single(writer.Rows);
        Assert.Equal(0, writer.PublishCount);
    }

    [Fact]
    public async Task Existing_index_snapshot_retry_discards_all_earlier_roots()
    {
        var fixture = new Fixture { ChangeSnapshotOnBOnce = true };
        fixture.Children[fixture.A] = [Path.Combine(fixture.A, "one")];
        fixture.Children[fixture.B] = [Path.Combine(fixture.B, "two")];
        var writer = new Writer();
        var updates = new List<CatalogOperationProgress>();
        await using var provider = Provider(fixture);
        var result = await provider.GetRequiredService<ICatalogService>().LoadExistingAsync(
            Request(fixture, fixture.A, fixture.B) with { ResultWriter = writer, RetainItems = false },
            new Progress(updates.Add));
        Assert.Equal([fixture.A, fixture.B, fixture.A, fixture.B], fixture.VisitedRoots);
        Assert.Equal(1, writer.ResetCount);
        Assert.Equal(4, writer.Rows.Count);
        Assert.Equal(4, result.MatchedCount);
        Assert.Empty(result.Items);
        Assert.Contains(updates, static update => update.AttemptReset && update.AttemptId == 2);
        Assert.Equal(2, updates.Last(static update => update.MatchedItem is not null).AttemptId);
    }

    [Fact]
    public async Task Every_entry_path_must_be_absolute_and_entry_count_is_bounded()
    {
        var fixture = new Fixture();
        await using var provider = Provider(fixture);
        var service = provider.GetRequiredService<ICatalogService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAndLoadAsync(Request(fixture, fixture.A, "relative")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAndLoadAsync(Request(fixture, Enumerable.Repeat(fixture.A, 65).ToArray())));
        Assert.Empty(fixture.VisitedRoots);
    }

    [Fact]
    public async Task Pending_scopes_from_an_earlier_index_root_are_not_lost()
    {
        var fixture = new Fixture { FirstRootHasPendingScopes = true };
        await using var provider = Provider(fixture);
        var result = await provider.GetRequiredService<ICatalogService>().LoadExistingAsync(Request(fixture, fixture.A, fixture.B));
        Assert.True(result.HasPendingScopes);
        Assert.Equal(2, result.MatchedCount);
    }

    private static CatalogRequest Request(Fixture fixture, params string[] roots) => new("test", fixture.A, "index.db") { EntryPaths = roots };
    private static ServiceProvider Provider(Fixture fixture, Trace? trace = null, IProfilePathCanonicalizer? canonicalizer = null)
    {
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(fixture);
        services.AddSingleton<IDirectoryDiscoveryService>(fixture);
        services.AddSingleton<IProfileResolver>(fixture);
        if (trace is not null) services.AddSingleton<ICatalogScanTraceSink>(trace);
        if (canonicalizer is not null) services.AddSingleton(canonicalizer);
        return services.BuildServiceProvider();
    }
    private sealed class Canonicalizer(string from, string to) : IProfilePathCanonicalizer
    { public string Canonicalize(string path) => path == from ? to : path; }
    private sealed class Progress(Action<CatalogOperationProgress> report) : IProgress<CatalogOperationProgress>
    { public void Report(CatalogOperationProgress value) => report(value); }
    private sealed class Trace : ICatalogScanTraceSink
    { public List<CatalogScanTraceEvent> Events { get; } = []; public void Report(CatalogScanTraceEvent value) => Events.Add(value); }
    private sealed class Writer : ICatalogProjectionWriter
    {
        public List<CatalogItem> Rows { get; } = [];
        public string Generation => "entries-test";
        public int Count => Rows.Count;
        public int ResetCount, PublishCount;
        public void Append(CatalogItem item) => Rows.Add(item);
        public void Flush() { }
        public void Reset() { ResetCount++; Rows.Clear(); }
        public CatalogProjectionSnapshot Publish(CatalogProjectionSummary summary, CatalogProjectionScope? publishedScope = null)
        { PublishCount++; throw new NotSupportedException(); }
        public void Dispose() { }
    }
    private sealed class Fixture : ILoadedProfile, IProfileResolver, IIndexSessionFactory, IDirectoryDiscoveryService
    {
        public string A { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "entries-a"));
        public string B { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "entries-b"));
        public string C { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "entries-c"));
        public int MaxDepth { get; init; } = 256;
        public string? ExcludedName { get; init; }
        public bool FailureRootIsB { get; init; }
        public bool CancelOnFailure { get; init; }
        public bool ChangeSnapshotOnBOnce { get; init; }
        public bool FirstRootHasPendingScopes { get; init; }
        private bool _snapshotChanged;
        public Dictionary<string, string[]> Children { get; } = [];
        public List<string> VisitedRoots { get; } = [];
        public List<string> MappedPaths { get; } = [];
        public Dictionary<(string Root, string Path), DirectoryTraversalDecision> Decisions { get; } = [];
        public ProfileDescriptor Descriptor => new("test", "1", "Entries", ProfileCandidateKind.Directory, [], []) { MaxScanDepth = MaxDepth };
        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            MappedPaths.Add(candidate.AbsolutePath);
            return ProfileMapResult.Success(new("test", candidate.AbsolutePath, "all", new object(),
                new Dictionary<string, object?> { ["name"] = Path.GetFileName(candidate.AbsolutePath) }));
        }
        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(string name) => name == ExcludedName
            ? ProfileDirectoryNameExclusionResult.Excluded("skip") : ProfileDirectoryNameExclusionResult.NotExcluded();
        public bool TryResolve(string id, [NotNullWhen(true)] out ILoadedProfile? profile)
        { profile = id == "test" ? this : null; return profile is not null; }
        public IIndexSession Create(string databasePath) => new Session(this, databasePath);
        private int Visit(string root, Func<DiscoveredDirectory, DirectoryTraversalDecision> visitor, CancellationToken token)
        {
            VisitedRoots.Add(root);
            if (FailureRootIsB && root == B)
            {
                if (CancelOnFailure) throw new OperationCanceledException(token);
                throw new IOException("Second root failed.");
            }
            var count = 0;
            Walk(root, 0);
            return count;
            void Walk(string path, int depth)
            {
                token.ThrowIfCancellationRequested();
                count++;
                var decision = visitor(Directory(path, depth));
                Decisions[(root, path)] = decision;
                if (decision == DirectoryTraversalDecision.Continue && Children.TryGetValue(path, out var children))
                    foreach (var child in children) Walk(child, depth + 1);
            }
        }
        private static DiscoveredDirectory Directory(string path, int depth) => new(path, Path.GetFileName(path),
            Path.GetDirectoryName(path)!, DateTimeOffset.MinValue, DateTimeOffset.MinValue) { Depth = depth };
        public Task<DirectoryDiscoveryReport> DiscoverAsync(DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitor, IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var count = Visit(request.RootPath, visitor, cancellationToken);
            var stats = new DirectoryDiscoveryProgress(count, count, 0, 0, 0, TimeSpan.FromSeconds(1));
            progress?.Report(stats);
            return Task.FromResult(new DirectoryDiscoveryReport(request.RootPath, DirectoryDiscoveryStatus.Completed, stats, []));
        }
        private sealed class Session(Fixture owner, string databasePath) : IIndexSession
        {
            public string DatabasePath => databasePath;
            public Task<IndexScanReport> ScanAsync(IndexScanRequest request, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default)
            {
                var count = owner.Visit(request.RootPath, request.InspectDirectory!, cancellationToken);
                var stats = new IndexScanProgress(count, count, 0, 0, 0, TimeSpan.FromSeconds(1), 0);
                progress?.Report(stats);
                return Task.FromResult(new IndexScanReport(Guid.NewGuid(), request.RootPath, request.RootPath, IndexScanStatus.Completed, stats, [], []));
            }
            public async Task<DirectorySearchResult> SearchDirectoriesAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default)
            {
                owner.VisitedRoots.Add(request.RootPath);
                var rows = owner.Children.GetValueOrDefault(request.RootPath, []).Select(path => new IndexedDirectory(path,
                    Path.GetFileName(path), request.RootPath, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false)).ToArray();
                if (request.PageConsumer is not null) await request.PageConsumer(rows, cancellationToken);
                if (owner.ChangeSnapshotOnBOnce && request.RootPath == owner.B && !owner._snapshotChanged)
                {
                    owner._snapshotChanged = true;
                    throw new IndexSnapshotChangedException(request.RootPath, Guid.NewGuid(), Guid.NewGuid());
                }
                return new(request.IncludeDirectoriesInResult ? rows : [], owner.FirstRootHasPendingScopes && request.RootPath == owner.A);
            }
            public Task<IndexRootStatus> GetRootStatusAsync(string rootPath, CancellationToken cancellationToken = default) => Task.FromResult(
                new IndexRootStatus(rootPath, IndexRootAvailability.Available, Guid.NewGuid(), rootPath, IndexScanStatus.Completed, DateTimeOffset.UtcNow, 1, 0, false));
            public Task<EntrySearchResult> SearchEntriesAsync(EntrySearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
