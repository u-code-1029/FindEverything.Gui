using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogServiceTests
{
    [Fact]
    public async Task Discover_uses_profile_pruning_without_creating_an_index_session()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-discovery-root"));
        var terminalPath = Path.Combine(root, "terminal");
        var hiddenPath = Path.Combine(terminalPath, "hidden");
        var siblingPath = Path.Combine(root, "sibling");
        var discovery = new FakeDiscoveryService(root, terminalPath, hiddenPath, siblingPath);
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(discovery);
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new PruningProfile()));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.DiscoverAsync(new CatalogRequest("test", root, "unused.db"));

        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(1, result.NoMatchCount);
        Assert.Equal(siblingPath, Assert.Single(result.Items).FullPath);
        Assert.Equal(terminalPath, Assert.Single(result.InvalidItems).FullPath);
        Assert.DoesNotContain(hiddenPath, discovery.VisitedPaths);
        Assert.Equal(
            DirectoryTraversalDecision.SkipDescendants,
            discovery.Decisions[terminalPath]);
        Assert.Equal(1, Assert.IsType<DirectoryDiscoveryReport>(result.DiscoveryReport)
            .Progress.PrunedDirectories);
        Assert.Null(result.ScanReport);
    }

    [Fact]
    public async Task LoadExisting_maps_matches_and_keeps_invalid_items_separate()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-root"));
        var goodPath = Path.Combine(root, "good");
        var badPath = Path.Combine(root, "bad");
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(new FakeSessionFactory([
            new IndexedDirectory(goodPath, "good", root, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false),
            new IndexedDirectory(badPath, "bad", root, DateTimeOffset.MinValue, DateTimeOffset.MinValue, true),
        ]));
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.LoadExistingAsync(new CatalogRequest("test", root, "index.db"));

        var item = Assert.Single(result.Items);
        Assert.Equal(goodPath, item.FullPath);
        Assert.Equal("good", item.Values["name"]);
        var invalid = Assert.Single(result.InvalidItems);
        Assert.Equal(badPath, invalid.FullPath);
        Assert.True(result.HasPendingScopes);
    }

    [Fact]
    public async Task ScanAndLoad_keeps_the_legacy_index_refresh_contract()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-scan-root"));
        var directoryPath = Path.Combine(root, "good");
        var sessionFactory = new FakeSessionFactory(
            [new IndexedDirectory(
                directoryPath,
                "good",
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                false)],
            allowScan: true);
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(sessionFactory);
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.ScanAndLoadAsync(new CatalogRequest("test", root, "index.db"));

        Assert.Equal(1, sessionFactory.ScanCalls);
        Assert.Equal(IndexScanStatus.Completed, Assert.IsType<IndexScanReport>(result.ScanReport).Status);
        Assert.Equal(directoryPath, Assert.Single(result.Items).FullPath);
        Assert.Null(result.DiscoveryReport);
    }

    private sealed class FakeSessionFactory(
        IReadOnlyList<IndexedDirectory> directories,
        bool allowScan = false) : IIndexSessionFactory
    {
        public int ScanCalls { get; private set; }

        public IIndexSession Create(string databasePath) =>
            new FakeSession(
                databasePath,
                directories,
                allowScan ? () => ScanCalls++ : null);
    }

    private sealed class ThrowingSessionFactory : IIndexSessionFactory
    {
        public IIndexSession Create(string databasePath) =>
            throw new InvalidOperationException("Direct discovery must not open the SQLite index.");
    }

    private sealed class FakeDiscoveryService(
        string root,
        string terminalPath,
        string hiddenPath,
        string siblingPath) : IDirectoryDiscoveryService
    {
        public List<string> VisitedPaths { get; } = [];

        public Dictionary<string, DirectoryTraversalDecision> Decisions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<DirectoryDiscoveryReport> DiscoverAsync(
            DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
            IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(root, request.RootPath);
            Visit(root, root);
            var terminalDecision = Visit(terminalPath, root);
            if (terminalDecision == DirectoryTraversalDecision.Continue)
            {
                Visit(hiddenPath, terminalPath);
            }

            Visit(siblingPath, root);
            var finalProgress = new DirectoryDiscoveryProgress(
                Entries: VisitedPaths.Count,
                Directories: VisitedPaths.Count,
                PrunedDirectories: Decisions.Values.Count(static decision =>
                    decision == DirectoryTraversalDecision.SkipDescendants),
                SkippedLinks: 0,
                ErrorCount: 0,
                Elapsed: TimeSpan.Zero);
            progress?.Report(finalProgress);
            return Task.FromResult(new DirectoryDiscoveryReport(
                root,
                DirectoryDiscoveryStatus.Completed,
                finalProgress,
                []));

            DirectoryTraversalDecision Visit(string path, string parentPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VisitedPaths.Add(path);
                var decision = visitDirectory(new DiscoveredDirectory(
                    path,
                    Path.GetFileName(path),
                    parentPath,
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue));
                Decisions[path] = decision;
                return decision;
            }
        }
    }

    private sealed class UnsupportedDiscoveryService : IDirectoryDiscoveryService
    {
        public Task<DirectoryDiscoveryReport> DiscoverAsync(
            DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
            IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSession(
        string databasePath,
        IReadOnlyList<IndexedDirectory> directories,
        Action? onScan) : IIndexSession
    {
        public string DatabasePath { get; } = databasePath;

        public Task<IndexScanReport> ScanAsync(
            IndexScanRequest request,
            IProgress<IndexScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (onScan is null)
            {
                throw new NotSupportedException();
            }

            onScan();
            return Task.FromResult(new IndexScanReport(
                Guid.NewGuid(),
                request.RootPath,
                request.ScopePath ?? request.RootPath,
                IndexScanStatus.Completed,
                new IndexScanProgress(0, 0, 0, 0, 0, TimeSpan.Zero, 0),
                [],
                []));
        }

        public Task<DirectorySearchResult> SearchDirectoriesAsync(
            DirectorySearchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DirectorySearchResult(directories, HasPendingScopes: false));

        public Task<EntrySearchResult> SearchEntriesAsync(
            EntrySearchRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IndexRootStatus> GetRootStatusAsync(
            string rootPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProfileResolver(ILoadedProfile profile) : IProfileResolver
    {
        public bool TryResolve(
            string profileId,
            [NotNullWhen(true)] out ILoadedProfile? resolved)
        {
            resolved = string.Equals(profileId, profile.Descriptor.Id, StringComparison.OrdinalIgnoreCase)
                ? profile
                : null;
            return resolved is not null;
        }
    }

    private sealed class FakeProfile : ILoadedProfile
    {
        private static readonly ProfileFieldDescriptor NameField = new(
            "name",
            "name",
            "이름",
            10,
            true,
            ProfileFieldValueKind.String,
            true,
            null,
            null);

        public ProfileDescriptor Descriptor { get; } = new(
            "test",
            "1.0.0",
            "Test",
            ProfileCandidateKind.Directory,
            ProfilePathInput.Relative,
            [NameField],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            if (candidate.RelativePath == "bad")
            {
                return ProfileMapResult.Invalid([
                    new ProfileMappingIssue("bad_path", "name", "Bad test path."),
                ]);
            }

            var values = new Dictionary<string, object?> { ["name"] = candidate.RelativePath };
            return ProfileMapResult.Success(new MappedProfileItem(
                "test",
                candidate.FullPath,
                candidate.RelativePath,
                "test-rule",
                new object(),
                values));
        }
    }

    private sealed class PruningProfile : ILoadedProfile
    {
        private static readonly ProfileFieldDescriptor NameField = new(
            "name",
            "name",
            "이름",
            10,
            true,
            ProfileFieldValueKind.String,
            true,
            null,
            null);

        public ProfileDescriptor Descriptor { get; } = new(
            "test",
            "1.0.0",
            "Test",
            ProfileCandidateKind.Directory,
            ProfilePathInput.Relative,
            [NameField],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            if (candidate.RelativePath == ".")
            {
                return ProfileMapResult.NoMatch();
            }

            if (candidate.RelativePath == "terminal")
            {
                return ProfileMapResult.Invalid(
                    [new ProfileMappingIssue("conversion", "name", "Invalid terminal value.")],
                    matchedRuleId: "terminal-rule",
                    shouldPruneDescendants: true);
            }

            var values = new Dictionary<string, object?> { ["name"] = candidate.RelativePath };
            return ProfileMapResult.Success(new MappedProfileItem(
                "test",
                candidate.FullPath,
                candidate.RelativePath,
                "test-rule",
                new object(),
                values));
        }
    }
}
