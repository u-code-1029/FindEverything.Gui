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
    public async Task Discover_reports_a_match_before_discovery_finishes()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-live-root"));
        var siblingPath = Path.Combine(root, "sibling");
        var discovery = new GatedMatchDiscoveryService(root, siblingPath);
        var matched = new TaskCompletionSource<CatalogItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedItems = new List<CatalogItem>();
        var reportedMessages = new List<string>();
        var reportedItemsGate = new object();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(discovery);
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new PruningProfile()));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();
        var progress = new InlineProgress<CatalogOperationProgress>(value =>
        {
            lock (reportedItemsGate)
            {
                reportedMessages.Add(value.Message);
            }

            if (value.MatchedItem is { } item)
            {
                lock (reportedItemsGate)
                {
                    reportedItems.Add(item);
                }

                matched.TrySetResult(item);
            }
        });

        var operation = service.DiscoverAsync(
            new CatalogRequest("test", root, "unused.db"),
            progress);
        CatalogItem liveItem;
        try
        {
            liveItem = await matched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(operation.IsCompleted);
            Assert.Equal(siblingPath, liveItem.FullPath);
            Assert.Equal("sibling", liveItem.Values["name"]);
        }
        finally
        {
            discovery.Complete();
        }

        var result = await operation;
        CatalogItem[] progressItems;
        string[] progressMessages;
        lock (reportedItemsGate)
        {
            progressItems = reportedItems.ToArray();
            progressMessages = reportedMessages.ToArray();
        }

        var progressItem = Assert.Single(progressItems);
        var finalItem = Assert.Single(result.Items);
        Assert.Equal(finalItem.FullPath, progressItem.FullPath);
        Assert.Equal(finalItem.MatchedRuleId, progressItem.MatchedRuleId);
        Assert.Equal(finalItem.Values, progressItem.Values);
        Assert.Equal(
            TimeSpan.FromSeconds(65.43),
            Assert.IsType<DirectoryDiscoveryReport>(result.DiscoveryReport).Progress.Elapsed);
        Assert.Contains(
            progressMessages,
            static message => message.Contains("소요 1분 05.43초", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1, "0.00초")]
    [InlineData(0, "0.00초")]
    [InlineData(12340, "12.34초")]
    [InlineData(65430, "1분 05.43초")]
    [InlineData(3723450, "1시간 02분 03.45초")]
    public void Catalog_elapsed_time_uses_a_consistent_Korean_format(
        int elapsedMilliseconds,
        string expected)
    {
        Assert.Equal(
            expected,
            CatalogElapsedTimeFormatter.Format(TimeSpan.FromMilliseconds(elapsedMilliseconds)));
    }

    [Fact]
    public async Task Discover_uses_profile_pruning_without_creating_an_index_session()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-discovery-root"));
        var terminalPath = Path.Combine(root, "terminal");
        var hiddenPath = Path.Combine(terminalPath, "hidden");
        var siblingPath = Path.Combine(root, "sibling");
        var discovery = new FakeDiscoveryService(root, terminalPath, hiddenPath, siblingPath);
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(discovery);
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new PruningProfile()));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

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

        Assert.Equal(
            [
                CatalogScanTraceKind.Started,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.Completed,
            ],
            trace.Events.Select(static entry => entry.Kind));
        Assert.Equal(
            Enumerable.Range(1, trace.Events.Count).Select(static value => (long)value),
            trace.Events.Select(static entry => entry.Sequence));
        Assert.Single(trace.Events.Select(static entry => entry.OperationId).Distinct());
        Assert.Contains("소요 0.00초", trace.Events[^1].Message, StringComparison.Ordinal);

        var visits = trace.Events
            .Where(static entry => entry.Kind == CatalogScanTraceKind.DirectoryVisited)
            .ToArray();
        Assert.Equal([root, terminalPath, siblingPath], visits.Select(static entry => entry.FullPath));
        Assert.Equal([".", "terminal", "sibling"], visits.Select(static entry => entry.RelativePath));
        Assert.Equal([root, terminalPath, siblingPath], visits.Select(static entry => entry.MatchInput));
        Assert.Equal(
            [ProfileMapStatus.NoMatch, ProfileMapStatus.Invalid, ProfileMapStatus.Success],
            visits.Select(static entry => entry.MappingStatus));
        Assert.Null(visits[0].MatchedRuleId);
        Assert.Equal("terminal-rule", visits[1].MatchedRuleId);
        Assert.Equal("test-rule", visits[2].MatchedRuleId);
        Assert.Empty(visits[0].Values);
        Assert.Empty(visits[1].Values);
        Assert.Equal("sibling", visits[2].Values["name"]);
        Assert.Equal("conversion", Assert.Single(visits[1].Issues).Code);
        Assert.Equal(DirectoryTraversalDecision.SkipDescendants, visits[1].TraversalDecision);
        Assert.Equal(DirectoryTraversalDecision.Continue, visits[2].TraversalDecision);
    }

    [Fact]
    public async Task Discover_keeps_the_scan_root_identity_but_maps_profiles_with_the_canonical_root()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-mapped-root"));
        var terminalPath = Path.Combine(root, "terminal");
        var hiddenPath = Path.Combine(terminalPath, "hidden");
        var siblingPath = Path.Combine(root, "sibling");
        var canonicalRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "unc",
            "192.168.10.20",
            "share"));
        var discovery = new FakeDiscoveryService(root, terminalPath, hiddenPath, siblingPath);
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IProfilePathCanonicalizer>(
            new PrefixCanonicalizer(root, canonicalRoot));
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(discovery);
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new PruningProfile()));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.DiscoverAsync(new CatalogRequest("test", root, "unused.db"));

        Assert.Equal([root, terminalPath, siblingPath], discovery.VisitedPaths);
        Assert.Equal(
            Path.Combine(canonicalRoot, "sibling"),
            Assert.Single(result.Items).FullPath);
        Assert.Equal(
            Path.Combine(canonicalRoot, "terminal"),
            Assert.Single(result.InvalidItems).FullPath);
        var visits = trace.Events
            .Where(static entry => entry.Kind == CatalogScanTraceKind.DirectoryVisited)
            .ToArray();
        Assert.All(visits, entry => Assert.Equal(root, entry.RootPath));
        Assert.Equal(
            [root, terminalPath, siblingPath],
            visits.Select(static entry => entry.FullPath));
        Assert.Equal(
            [
                canonicalRoot,
                Path.Combine(canonicalRoot, "terminal"),
                Path.Combine(canonicalRoot, "sibling"),
            ],
            visits.Select(static entry => entry.MatchInput));
    }

    [Fact]
    public async Task Discover_publishes_discovery_errors_before_the_completed_event()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-trace-errors"));
        var deniedPath = Path.Combine(root, "denied");
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(
            new ReportingDiscoveryService(
                root,
                DirectoryDiscoveryStatus.Partial,
                [new DirectoryDiscoveryError(deniedPath, "Access denied.")]));
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.DiscoverAsync(new CatalogRequest("test", root, "unused.db"));

        Assert.Equal(DirectoryDiscoveryStatus.Partial, result.DiscoveryReport?.Status);
        Assert.Equal(
            [
                CatalogScanTraceKind.Started,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DiscoveryError,
                CatalogScanTraceKind.Completed,
            ],
            trace.Events.Select(static entry => entry.Kind));
        var error = trace.Events[^2];
        Assert.Equal(deniedPath, error.FullPath);
        Assert.Equal("denied", error.RelativePath);
        Assert.Equal("Access denied.", error.Message);
    }

    [Fact]
    public async Task Discover_publishes_directory_exclusion_issues_and_continues_mapping()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-exclusion-issue"));
        var childPath = Path.Combine(root, "slow-name");
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(
            new SingleChildDiscoveryService(root, childPath));
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(
            new DirectoryExclusionProfile(reportIssue: true)));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ICatalogService>().DiscoverAsync(
            new CatalogRequest("test", root, "unused.db"));

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(0, result.ExcludedDirectoryCount);
        var exclusionIssue = Assert.Single(result.DirectoryExclusionIssues);
        Assert.Equal(childPath, exclusionIssue.FullPath);
        Assert.Equal(
            "directory_exclusion_regex_timeout",
            Assert.Single(exclusionIssue.Issues).Code);
        var issueTrace = Assert.Single(
            trace.Events,
            static entry => entry.Kind == CatalogScanTraceKind.DirectoryExclusionIssue);
        Assert.Equal(childPath, issueTrace.FullPath);
        Assert.Equal("slow-name", issueTrace.MatchInput);
        Assert.Equal(
            "directory_exclusion_regex_timeout",
            Assert.Single(issueTrace.Issues).Code);
        Assert.Contains(
            trace.Events,
            entry => entry.Kind == CatalogScanTraceKind.DirectoryVisited
                && entry.FullPath == childPath);
    }

    [Fact]
    public async Task Discover_publishes_cancelled_and_failed_terminal_events()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-trace-terminal"));

        var cancelledTrace = new RecordingTraceSink();
        var cancelledServices = new ServiceCollection();
        cancelledServices.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        cancelledServices.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        cancelledServices.AddSingleton<IDirectoryDiscoveryService>(
            new ReportingDiscoveryService(root, DirectoryDiscoveryStatus.Cancelled, []));
        cancelledServices.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));
        cancelledServices.AddSingleton<ICatalogScanTraceSink>(cancelledTrace);
        await using (var provider = cancelledServices.BuildServiceProvider())
        {
            var service = provider.GetRequiredService<ICatalogService>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.DiscoverAsync(new CatalogRequest("test", root, "unused.db")));
        }

        Assert.Equal(
            CatalogScanTraceKind.Cancelled,
            cancelledTrace.Events[^1].Kind);
        Assert.DoesNotContain(
            cancelledTrace.Events,
            static entry => entry.Kind is CatalogScanTraceKind.Completed or CatalogScanTraceKind.Failed);

        var failedTrace = new RecordingTraceSink();
        var failedServices = new ServiceCollection();
        failedServices.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        failedServices.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        failedServices.AddSingleton<IDirectoryDiscoveryService, ThrowingDiscoveryService>();
        failedServices.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));
        failedServices.AddSingleton<ICatalogScanTraceSink>(failedTrace);
        await using (var provider = failedServices.BuildServiceProvider())
        {
            var service = provider.GetRequiredService<ICatalogService>();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DiscoverAsync(new CatalogRequest("test", root, "unused.db")));
            Assert.Equal("Discovery failed.", exception.Message);
        }

        Assert.Equal(
            [CatalogScanTraceKind.Started, CatalogScanTraceKind.Failed],
            failedTrace.Events.Select(static entry => entry.Kind));
        Assert.Contains("Discovery failed.", failedTrace.Events[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_ignores_trace_sink_failures()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-trace-sink-failure"));
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory, ThrowingSessionFactory>();
        services.AddSingleton<IDirectoryDiscoveryService>(
            new ReportingDiscoveryService(root, DirectoryDiscoveryStatus.Completed, []));
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));
        services.AddSingleton<ICatalogScanTraceSink, ThrowingTraceSink>();

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.DiscoverAsync(new CatalogRequest("test", root, "unused.db"));

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(DirectoryDiscoveryStatus.Completed, result.DiscoveryReport?.Status);
    }

    [Fact]
    public async Task LoadExisting_maps_matches_and_keeps_invalid_items_separate()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-root"));
        var goodPath = Path.Combine(root, "good");
        var badPath = Path.Combine(root, "bad");
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(new FakeSessionFactory([
            new IndexedDirectory(goodPath, "good", root, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false),
            new IndexedDirectory(badPath, "bad", root, DateTimeOffset.MinValue, DateTimeOffset.MinValue, true),
        ]));
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.LoadExistingAsync(new CatalogRequest("test", root, "index.db"));

        var item = Assert.Single(result.Items);
        Assert.Equal(goodPath, item.FullPath);
        Assert.Equal("good", item.Values["name"]);
        var invalid = Assert.Single(result.InvalidItems);
        Assert.Equal(badPath, invalid.FullPath);
        Assert.True(result.HasPendingScopes);
        Assert.Empty(trace.Events);
    }

    [Theory]
    [InlineData(IndexRootAvailability.DatabaseMissing, "does not exist")]
    [InlineData(IndexRootAvailability.RootNotIndexed, "exact root")]
    public async Task LoadExisting_reports_why_the_index_cannot_be_loaded_before_searching(
        IndexRootAvailability availability,
        string expectedMessage)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-unavailable-root"));
        var databasePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-unavailable.db"));
        var sessionFactory = new FakeSessionFactory([], rootAvailability: availability);
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(sessionFactory);
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));

        await using var provider = services.BuildServiceProvider();
        var exception = await Assert.ThrowsAsync<CatalogIndexUnavailableException>(() =>
            provider.GetRequiredService<ICatalogService>().LoadExistingAsync(
                new CatalogRequest("test", root, databasePath)));

        Assert.Equal(availability, exception.Availability);
        Assert.Equal(databasePath, exception.DatabasePath);
        Assert.Equal(root, exception.RootPath);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(root, sessionFactory.LastStatusRoot);
        Assert.Null(sessionFactory.LastSearchRoot);
    }

    [Fact]
    public async Task LoadExisting_does_not_apply_direct_discovery_directory_exclusions()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-index-exclusion"));
        var directoryPath = Path.Combine(root, "excluded");
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(new FakeSessionFactory([
            new IndexedDirectory(
                directoryPath,
                "excluded",
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                false),
        ]));
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(
            new DirectoryExclusionProfile(excludedName: "excluded")));

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ICatalogService>().LoadExistingAsync(
            new CatalogRequest("test", root, "index.db"));

        Assert.Equal(directoryPath, Assert.Single(result.Items).FullPath);
        Assert.Equal(0, result.ExcludedDirectoryCount);
        Assert.Empty(result.DirectoryExclusionIssues);
    }

    [Fact]
    public async Task LoadExisting_evaluates_the_root_candidate_even_when_it_has_no_index_row()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-root-candidate"));
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(new FakeSessionFactory([]));
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(
            new ExactPathProfile(root)));

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ICatalogService>().LoadExistingAsync(
            new CatalogRequest("test", root, "index.db"));

        var item = Assert.Single(result.Items);
        Assert.Equal(root, item.FullPath);
        Assert.Equal(".", item.RelativePath);
        Assert.Equal(1, result.CandidateCount);
    }

    [Fact]
    public async Task LoadExisting_queries_the_raw_index_root_and_maps_results_with_the_canonical_root()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-index-mapped-root"));
        var canonicalRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "unc",
            "192.168.10.20",
            "indexed-share"));
        var directoryPath = Path.Combine(root, "good");
        var sessionFactory = new FakeSessionFactory([
            new IndexedDirectory(
                directoryPath,
                "good",
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                false),
        ]);
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IProfilePathCanonicalizer>(
            new PrefixCanonicalizer(root, canonicalRoot));
        services.AddSingleton<IIndexSessionFactory>(sessionFactory);
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ICatalogService>().LoadExistingAsync(
            new CatalogRequest("test", root, "index.db"));

        Assert.Equal(root, sessionFactory.LastSearchRoot);
        Assert.Equal(
            Path.Combine(canonicalRoot, "good"),
            Assert.Single(result.Items).FullPath);
    }

    [Fact]
    public async Task ScanAndLoad_preserves_profile_leaf_exclusion_semantics_and_mapping_trace()
    {
        // The selected root intentionally has the excluded leaf name. Direct discovery treats
        // it as the explicit boundary while still excluding matching descendants.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-scan-root", "cache"));
        var directoryPath = Path.Combine(root, "good");
        var excludedPath = Path.Combine(root, "cache");
        var sessionFactory = new FakeSessionFactory(
            [
                new IndexedDirectory(
                    directoryPath,
                    "good",
                    root,
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue,
                    false),
                new IndexedDirectory(
                    excludedPath,
                    "cache",
                    root,
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue,
                    true),
            ],
            allowScan: true);
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(sessionFactory);
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(
            new DirectoryExclusionProfile(excludedName: "cache")));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.ScanAndLoadAsync(new CatalogRequest("test", root, "index.db"));

        Assert.Equal(1, sessionFactory.ScanCalls);
        Assert.Empty(Assert.IsType<IndexScanRequest>(sessionFactory.LastScanRequest)
            .Options.ExcludedDirectoryNameRegexes);
        Assert.Equal(IndexScanStatus.Completed, Assert.IsType<IndexScanReport>(result.ScanReport).Status);
        Assert.Equal(directoryPath, Assert.Single(result.Items).FullPath);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(1, result.ExcludedDirectoryCount);
        Assert.False(result.HasPendingScopes);
        Assert.Null(sessionFactory.LastSearchRoot);
        Assert.Null(result.DiscoveryReport);
        Assert.Equal(
            [
                CatalogScanTraceKind.Started,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DirectoryExcluded,
                CatalogScanTraceKind.Completed,
            ],
            trace.Events.Select(static entry => entry.Kind));
        Assert.Equal(
            [root, directoryPath],
            trace.Events
                .Where(static entry => entry.Kind == CatalogScanTraceKind.DirectoryVisited)
                .Select(static entry => entry.FullPath));
        var excludedTrace = Assert.Single(
            trace.Events,
            static entry => entry.Kind == CatalogScanTraceKind.DirectoryExcluded);
        Assert.Equal(excludedPath, excludedTrace.FullPath);
        Assert.Equal("test-exclusion", excludedTrace.MatchedRuleId);
        Assert.Equal(
            DirectoryTraversalDecision.ExcludeSubtree,
            excludedTrace.TraversalDecision);
        Assert.Contains("DB 저장", trace.Events[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAndLoad_publishes_leaf_exclusion_issues_and_fails_open()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-index-exclusion-issue"));
        var childPath = Path.Combine(root, "slow-name");
        var sessionFactory = new FakeSessionFactory(
            [new IndexedDirectory(
                childPath,
                "slow-name",
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                false)],
            allowScan: true);
        var trace = new RecordingTraceSink();
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(sessionFactory);
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(
            new DirectoryExclusionProfile(reportIssue: true)));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(
            new CatalogRequest("test", root, "index.db"));

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(0, result.ExcludedDirectoryCount);
        var exclusionIssue = Assert.Single(result.DirectoryExclusionIssues);
        Assert.Equal(childPath, exclusionIssue.FullPath);
        Assert.Equal(
            "directory_exclusion_regex_timeout",
            Assert.Single(exclusionIssue.Issues).Code);
        var issueTrace = Assert.Single(
            trace.Events,
            static entry => entry.Kind == CatalogScanTraceKind.DirectoryExclusionIssue);
        Assert.Equal(childPath, issueTrace.FullPath);
        Assert.Contains(
            trace.Events,
            entry => entry.Kind == CatalogScanTraceKind.DirectoryVisited
                && entry.FullPath == childPath);
    }

    [Fact]
    public async Task ScanAndLoadReportsLiveMatchesAndUsesProfilePruningWithoutReadingTheIndex()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-live-index-root"));
        var terminalPath = Path.Combine(root, "terminal");
        var hiddenPath = Path.Combine(terminalPath, "hidden");
        var siblingPath = Path.Combine(root, "sibling");
        var sessionFactory = new GatedInspectionSessionFactory(
            root,
            terminalPath,
            hiddenPath,
            siblingPath);
        var trace = new RecordingTraceSink();
        var matched = new TaskCompletionSource<CatalogItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(sessionFactory);
        services.AddSingleton<IDirectoryDiscoveryService, UnsupportedDiscoveryService>();
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new PruningProfile()));
        services.AddSingleton<ICatalogScanTraceSink>(trace);

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();
        var operation = service.ScanAndLoadAsync(
            new CatalogRequest("test", root, "index.db"),
            new InlineProgress<CatalogOperationProgress>(value =>
            {
                if (value.MatchedItem is { } item)
                    matched.TrySetResult(item);
            }));

        CatalogItem liveItem;
        try
        {
            liveItem = await matched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(operation.IsCompleted);
            Assert.Equal(siblingPath, liveItem.FullPath);
            Assert.True(liveItem.CoveragePending);
        }
        finally
        {
            sessionFactory.Complete();
        }

        var result = await operation;
        var finalItem = Assert.Single(result.Items);
        Assert.Equal(liveItem.FullPath, finalItem.FullPath);
        Assert.True(finalItem.CoveragePending);
        Assert.True(result.HasPendingScopes);
        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(1, result.NoMatchCount);
        Assert.Equal(terminalPath, Assert.Single(result.InvalidItems).FullPath);
        Assert.DoesNotContain(hiddenPath, sessionFactory.VisitedPaths);
        Assert.Equal(
            DirectoryTraversalDecision.SkipDescendants,
            sessionFactory.Decisions[terminalPath]);
        Assert.Equal(0, sessionFactory.SearchCalls);
        Assert.Equal(
            [
                CatalogScanTraceKind.Started,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DirectoryVisited,
                CatalogScanTraceKind.DiscoveryError,
                CatalogScanTraceKind.Completed,
            ],
            trace.Events.Select(static entry => entry.Kind));
        var error = trace.Events[^2];
        Assert.Equal(Path.Combine(root, "denied"), error.FullPath);
        Assert.Equal("Access denied.", error.Message);
    }

    private sealed class GatedInspectionSessionFactory(
        string root,
        string terminalPath,
        string hiddenPath,
        string siblingPath) : IIndexSessionFactory
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _root = root;
        private readonly string _terminalPath = terminalPath;
        private readonly string _hiddenPath = hiddenPath;
        private readonly string _siblingPath = siblingPath;

        public List<string> VisitedPaths { get; } = [];

        public Dictionary<string, DirectoryTraversalDecision> Decisions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int SearchCalls { get; private set; }

        public IIndexSession Create(string databasePath) => new Session(this, databasePath);

        public void Complete() => _release.TrySetResult();

        private sealed class Session(
            GatedInspectionSessionFactory owner,
            string databasePath) : IIndexSession
        {
            public string DatabasePath { get; } = databasePath;

            public async Task<IndexScanReport> ScanAsync(
                IndexScanRequest request,
                IProgress<IndexScanProgress>? progress = null,
                CancellationToken cancellationToken = default)
            {
                var inspectDirectory = request.InspectDirectory
                    ?? throw new InvalidOperationException("The catalog scan must inspect directories inline.");
                Visit(owner._root, owner._root, coveragePending: false);
                var terminalDecision = Visit(
                    owner._terminalPath,
                    owner._root,
                    coveragePending: false);
                if (terminalDecision == DirectoryTraversalDecision.Continue)
                    Visit(owner._hiddenPath, owner._terminalPath, coveragePending: false);
                Visit(owner._siblingPath, owner._root, coveragePending: true);

                await owner._release.Task.WaitAsync(cancellationToken);
                var scanProgress = new IndexScanProgress(
                    Entries: owner.VisitedPaths.Count,
                    Directories: owner.VisitedPaths.Count,
                    ExcludedEntries: 0,
                    SkippedLinks: 0,
                    ErrorCount: 1,
                    Elapsed: TimeSpan.Zero,
                    PendingDirectories: 1);
                progress?.Report(scanProgress);
                return new IndexScanReport(
                    Guid.NewGuid(),
                    request.RootPath,
                    request.ScopePath ?? request.RootPath,
                    IndexScanStatus.Partial,
                    scanProgress,
                    [new IndexScanError(Path.Combine(owner._root, "denied"), "Access denied.")],
                    [new IndexPendingScope(
                        request.RootPath,
                        owner._siblingPath,
                        IndexDeferralReason.ExplicitRule,
                        null,
                        null,
                        DateTimeOffset.UtcNow)]);

                DirectoryTraversalDecision Visit(
                    string path,
                    string parentPath,
                    bool coveragePending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    owner.VisitedPaths.Add(path);
                    var decision = inspectDirectory(new DiscoveredDirectory(
                        path,
                        Path.GetFileName(path),
                        parentPath,
                        DateTimeOffset.MinValue,
                        DateTimeOffset.MinValue)
                    {
                        CoveragePending = coveragePending,
                    });
                    owner.Decisions[path] = decision;
                    return decision;
                }
            }

            public Task<DirectorySearchResult> SearchDirectoriesAsync(
                DirectorySearchRequest request,
                CancellationToken cancellationToken = default)
            {
                owner.SearchCalls++;
                throw new InvalidOperationException("A single-pass catalog scan must not reread the index.");
            }

            public Task<EntrySearchResult> SearchEntriesAsync(
                EntrySearchRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<IndexRootStatus> GetRootStatusAsync(
                string rootPath,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSessionFactory(
        IReadOnlyList<IndexedDirectory> directories,
        bool allowScan = false,
        IndexRootAvailability rootAvailability = IndexRootAvailability.Available) : IIndexSessionFactory
    {
        public int ScanCalls { get; private set; }

        public string? LastSearchRoot { get; private set; }

        public string? LastStatusRoot { get; private set; }

        public IndexScanRequest? LastScanRequest { get; private set; }

        public IIndexSession Create(string databasePath) =>
            new FakeSession(
                databasePath,
                directories,
                allowScan
                    ? request =>
                    {
                        ScanCalls++;
                        LastScanRequest = request;
                    }
                    : null,
                rootPath => LastSearchRoot = rootPath,
                rootPath => LastStatusRoot = rootPath,
                rootAvailability);
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
                    decision != DirectoryTraversalDecision.Continue),
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

    private sealed class GatedMatchDiscoveryService(string root, string matchPath)
        : IDirectoryDiscoveryService
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DirectoryDiscoveryReport> DiscoverAsync(
            DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
            IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _ = visitDirectory(new DiscoveredDirectory(
                root,
                Path.GetFileName(root),
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue));
            _ = visitDirectory(new DiscoveredDirectory(
                matchPath,
                Path.GetFileName(matchPath),
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue));

            await _release.Task.WaitAsync(cancellationToken);
            var reportProgress = new DirectoryDiscoveryProgress(
                Entries: 2,
                Directories: 2,
                PrunedDirectories: 0,
                SkippedLinks: 0,
                ErrorCount: 0,
                Elapsed: TimeSpan.FromSeconds(65.43));
            progress?.Report(reportProgress);
            return new DirectoryDiscoveryReport(
                root,
                DirectoryDiscoveryStatus.Completed,
                reportProgress,
                []);
        }

        public void Complete() => _release.TrySetResult();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ReportingDiscoveryService(
        string root,
        DirectoryDiscoveryStatus status,
        IReadOnlyList<DirectoryDiscoveryError> errors) : IDirectoryDiscoveryService
    {
        public Task<DirectoryDiscoveryReport> DiscoverAsync(
            DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
            IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var decision = visitDirectory(new DiscoveredDirectory(
                root,
                Path.GetFileName(root),
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue));
            var reportProgress = new DirectoryDiscoveryProgress(
                Entries: 1,
                Directories: 1,
                PrunedDirectories: decision == DirectoryTraversalDecision.Continue ? 0 : 1,
                SkippedLinks: 0,
                ErrorCount: errors.Count,
                Elapsed: TimeSpan.Zero);
            progress?.Report(reportProgress);
            return Task.FromResult(new DirectoryDiscoveryReport(
                root,
                status,
                reportProgress,
                errors));
        }
    }

    private sealed class SingleChildDiscoveryService(
        string root,
        string childPath) : IDirectoryDiscoveryService
    {
        public Task<DirectoryDiscoveryReport> DiscoverAsync(
            DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
            IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _ = visitDirectory(new DiscoveredDirectory(
                root,
                Path.GetFileName(root),
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue));
            _ = visitDirectory(new DiscoveredDirectory(
                childPath,
                Path.GetFileName(childPath),
                root,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue));
            var reportProgress = new DirectoryDiscoveryProgress(
                Entries: 2,
                Directories: 2,
                PrunedDirectories: 0,
                SkippedLinks: 0,
                ErrorCount: 0,
                Elapsed: TimeSpan.Zero);
            return Task.FromResult(new DirectoryDiscoveryReport(
                root,
                DirectoryDiscoveryStatus.Completed,
                reportProgress,
                []));
        }
    }

    private sealed class ThrowingDiscoveryService : IDirectoryDiscoveryService
    {
        public Task<DirectoryDiscoveryReport> DiscoverAsync(
            DirectoryDiscoveryRequest request,
            Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
            IProgress<DirectoryDiscoveryProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Discovery failed.");
    }

    private sealed class RecordingTraceSink : ICatalogScanTraceSink
    {
        public List<CatalogScanTraceEvent> Events { get; } = [];

        public void Report(CatalogScanTraceEvent value) => Events.Add(value);
    }

    private sealed class ThrowingTraceSink : ICatalogScanTraceSink
    {
        public void Report(CatalogScanTraceEvent value) =>
            throw new InvalidOperationException("The observer must not affect discovery.");
    }

    private sealed class FakeSession(
        string databasePath,
        IReadOnlyList<IndexedDirectory> directories,
        Action<IndexScanRequest>? onScan,
        Action<string> onSearch,
        Action<string> onStatus,
        IndexRootAvailability rootAvailability) : IIndexSession
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

            onScan(request);
            if (request.InspectDirectory is { } inspectDirectory)
            {
                var trimmedRoot = Path.TrimEndingDirectorySeparator(request.RootPath);
                _ = inspectDirectory(new DiscoveredDirectory(
                    request.RootPath,
                    Path.GetFileName(trimmedRoot),
                    Path.GetDirectoryName(trimmedRoot) ?? request.RootPath,
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue));
                foreach (var directory in directories)
                {
                    _ = inspectDirectory(new DiscoveredDirectory(
                        directory.FullPath,
                        directory.Name,
                        directory.ParentPath,
                        directory.CreatedUtc,
                        directory.ModifiedUtc)
                    {
                        CoveragePending = directory.CoveragePending,
                    });
                }
            }

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
            CancellationToken cancellationToken = default)
        {
            onSearch(request.RootPath);
            request.PageProgress?.Report(directories);
            return Task.FromResult(new DirectorySearchResult(
                directories,
                HasPendingScopes: false));
        }

        public Task<EntrySearchResult> SearchEntriesAsync(
            EntrySearchRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IndexRootStatus> GetRootStatusAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStatus(rootPath);
            return Task.FromResult(new IndexRootStatus(
                rootPath,
                rootAvailability,
                rootAvailability == IndexRootAvailability.Available ? Guid.NewGuid() : null,
                rootAvailability == IndexRootAvailability.Available ? rootPath : null,
                rootAvailability == IndexRootAvailability.Available ? IndexScanStatus.Completed : null,
                rootAvailability == IndexRootAvailability.Available ? DateTimeOffset.UtcNow : null,
                rootAvailability == IndexRootAvailability.Available ? directories.Count : 0,
                0,
                false));
        }

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

    private sealed class PrefixCanonicalizer(string sourceRoot, string canonicalRoot)
        : IProfilePathCanonicalizer
    {
        public string Canonicalize(string path)
        {
            var absolutePath = Path.GetFullPath(path);
            if (absolutePath.Equals(sourceRoot, StringComparison.Ordinal))
            {
                return canonicalRoot;
            }

            var prefix = sourceRoot + Path.DirectorySeparatorChar;
            return absolutePath.StartsWith(prefix, StringComparison.Ordinal)
                ? Path.Combine(canonicalRoot, absolutePath[prefix.Length..])
                : absolutePath;
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
            [NameField],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            var name = Path.GetFileName(candidate.AbsolutePath);
            if (name == "bad")
            {
                return ProfileMapResult.Invalid([
                    new ProfileMappingIssue("bad_path", "name", "Bad test path."),
                ]);
            }

            if (name is not ("good" or "excluded"))
            {
                return ProfileMapResult.NoMatch();
            }

            var values = new Dictionary<string, object?> { ["name"] = name };
            return ProfileMapResult.Success(new MappedProfileItem(
                "test",
                candidate.AbsolutePath,
                "test-rule",
                new object(),
                values));
        }
    }

    private sealed class ExactPathProfile(string expectedPath) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(
            "test",
            "1.0.0",
            "Test",
            ProfileCandidateKind.Directory,
            [],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate) =>
            string.Equals(candidate.AbsolutePath, expectedPath, StringComparison.Ordinal)
                ? ProfileMapResult.Success(new MappedProfileItem(
                    "test",
                    candidate.AbsolutePath,
                    "root-rule",
                    new object(),
                    new Dictionary<string, object?>()))
                : ProfileMapResult.NoMatch();
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
            [NameField],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            var name = Path.GetFileName(candidate.AbsolutePath);

            if (name == "terminal")
            {
                return ProfileMapResult.Invalid(
                    [new ProfileMappingIssue("conversion", "name", "Invalid terminal value.")],
                    matchedRuleId: "terminal-rule",
                    shouldPruneDescendants: true);
            }

            if (name != "sibling")
            {
                return ProfileMapResult.NoMatch();
            }

            var values = new Dictionary<string, object?> { ["name"] = name };
            return ProfileMapResult.Success(new MappedProfileItem(
                "test",
                candidate.AbsolutePath,
                "test-rule",
                new object(),
                values));
        }
    }

    private sealed class DirectoryExclusionProfile(
        string? excludedName = null,
        bool reportIssue = false) : ILoadedProfile
    {
        private readonly FakeProfile _inner = new();

        public ProfileDescriptor Descriptor => _inner.Descriptor;

        public ProfileMapResult Map(ProfilePathCandidate candidate) => _inner.Map(candidate);

        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(string directoryName)
        {
            if (reportIssue)
            {
                return new ProfileDirectoryNameExclusionResult(
                    false,
                    null,
                    [new ProfileMappingIssue(
                        "directory_exclusion_regex_timeout",
                        null,
                        "Test timeout.")]);
            }

            return string.Equals(directoryName, excludedName, StringComparison.Ordinal)
                ? ProfileDirectoryNameExclusionResult.Excluded("test-exclusion")
                : ProfileDirectoryNameExclusionResult.NotExcluded();
        }
    }
}
