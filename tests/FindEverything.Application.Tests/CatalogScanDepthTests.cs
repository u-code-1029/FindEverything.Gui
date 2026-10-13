using System.Diagnostics.CodeAnalysis;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogScanDepthTests
{
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task New_scan_keeps_boundary_folder_and_does_not_visit_its_children(int maxDepth)
    {
        var tree = new Tree(); var profile = new Profile(maxDepth);
        await using var provider = Provider(tree, profile);
        var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(Request(tree));
        Assert.Equal(maxDepth + 1, result.MatchedCount);
        Assert.Equal(maxDepth, tree.LastScan!.Options.MaxDepth);
        Assert.Equal(DirectoryTraversalDecision.SkipDescendants, tree.Decisions[maxDepth]);
        Assert.Equal(ProfileFieldResourceState.Error, result.Items[0].Resources["ready"].State);
        Assert.Equal("unable to inspect", result.Items[0].FieldErrors["ready"]);
        Assert.Equal(maxDepth + 1, result.FieldIssueRowCount);
        Assert.Equal(0, result.InvalidCount);
    }
    [Fact]
    public async Task Discovery_without_metadata_writes_obeys_the_same_depth_boundary()
    {
        var tree = new Tree(); await using var provider = Provider(tree, new Profile(1));
        var result = await provider.GetRequiredService<ICatalogService>().DiscoverAsync(Request(tree));
        Assert.Equal(2, result.MatchedCount); Assert.Equal(1, tree.LastDiscovery!.MaxDepth);
        Assert.Equal(DirectoryTraversalDecision.SkipDescendants, tree.Decisions[1]);
    }
    [Fact]
    public async Task Explicit_stored_metadata_rebuild_does_not_retroactively_apply_scan_depth()
    {
        var tree = new Tree(); await using var provider = Provider(tree, new Profile(0));
        var result = await provider.GetRequiredService<ICatalogService>().LoadExistingAsync(Request(tree));
        Assert.Equal(4, result.MatchedCount); Assert.Null(tree.LastScan);
    }
    private static CatalogRequest Request(Tree tree) => new("test", tree.Root, Path.Combine(Path.GetTempPath(), "depth-test.db"));
    private static ServiceProvider Provider(Tree tree, Profile profile)
    {
        var services = new ServiceCollection(); services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(tree); services.AddSingleton<IDirectoryDiscoveryService>(tree);
        services.AddSingleton<IProfileResolver>(profile); return services.BuildServiceProvider();
    }
    private sealed class Profile(int maxDepth) : ILoadedProfile, IProfileResolver
    {
        public ProfileDescriptor Descriptor { get; } = new("test", "1", "Depth", ProfileCandidateKind.Directory,
            [new("name", "name", "Name", 0, true, ProfileFieldValueKind.String, false, null, null)], []) { MaxScanDepth = maxDepth };
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.Success(new("test", candidate.AbsolutePath, "all", new object(),
            new Dictionary<string, object?> { ["name"] = candidate.AbsolutePath })
        { Resources = new Dictionary<string, ProfileFieldResourceInfo> { ["ready"] = new(ProfileFieldResourceState.Error) },
          FieldErrors = new Dictionary<string, string> { ["ready"] = "unable to inspect" } });
        public bool TryResolve(string id, [NotNullWhen(true)] out ILoadedProfile? profile) { profile = id == "test" ? this : null; return profile is not null; }
    }
    private sealed class Tree : IIndexSessionFactory, IDirectoryDiscoveryService
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "depth-root"));
        public IndexScanRequest? LastScan; public DirectoryDiscoveryRequest? LastDiscovery;
        public Dictionary<int, DirectoryTraversalDecision> Decisions { get; } = [];
        public IIndexSession Create(string databasePath) => new Session(this, databasePath);
        public Task<DirectoryDiscoveryReport> DiscoverAsync(DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitor, IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastDiscovery = request; Visit(visitor); return Task.FromResult(new DirectoryDiscoveryReport(Root, DirectoryDiscoveryStatus.Completed,
                new(0, Decisions.Count, 1, 0, 0, TimeSpan.Zero), []));
        }
        private string FullPath(int depth) => depth == 0 ? Root : Path.Combine(Root, Path.Combine(Enumerable.Range(1, depth).Select(d => "d" + d).ToArray()));
        private void Visit(Func<DiscoveredDirectory, DirectoryTraversalDecision> visitor)
        {
            for (var depth = 0; depth <= 3; depth++)
            {
                var decision = visitor(new(FullPath(depth), "d" + depth, depth == 0 ? Root : FullPath(depth - 1), DateTimeOffset.MinValue, DateTimeOffset.MinValue) { Depth = depth });
                Decisions[depth] = decision; if (decision != DirectoryTraversalDecision.Continue) break;
            }
        }
        private sealed class Session(Tree owner, string databasePath) : IIndexSession
        {
            public string DatabasePath => databasePath;
            public Task<IndexScanReport> ScanAsync(IndexScanRequest request, IProgress<IndexScanProgress>? progress = null, CancellationToken cancellationToken = default)
            {
                owner.LastScan = request; owner.Visit(request.InspectDirectory!);
                return Task.FromResult(new IndexScanReport(Guid.NewGuid(), owner.Root, owner.Root, IndexScanStatus.Completed, new(0, 0, 0, 0, 0, TimeSpan.Zero, 0), [], []));
            }
            public async Task<DirectorySearchResult> SearchDirectoriesAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default)
            {
                var rows = Enumerable.Range(1, 3).Select(depth => new IndexedDirectory(owner.FullPath(depth), "d" + depth, owner.FullPath(depth - 1), DateTimeOffset.MinValue, DateTimeOffset.MinValue, false)).ToArray();
                if (request.PageConsumer is not null) await request.PageConsumer(rows, cancellationToken);
                return new(request.IncludeDirectoriesInResult ? rows : [], false);
            }
            public Task<IndexRootStatus> GetRootStatusAsync(string rootPath, CancellationToken cancellationToken = default) => Task.FromResult(
                new IndexRootStatus(rootPath, IndexRootAvailability.Available, Guid.NewGuid(), rootPath, IndexScanStatus.Completed, DateTimeOffset.UtcNow, 3, 0, false));
            public Task<EntrySearchResult> SearchEntriesAsync(EntrySearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
