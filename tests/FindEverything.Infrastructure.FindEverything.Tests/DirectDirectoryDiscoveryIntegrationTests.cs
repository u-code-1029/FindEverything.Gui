using FindEverything.Application;
using FindEverything.Application.Catalog;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Xunit;

namespace FindEverything.Infrastructure.FindEverything.Tests;

public sealed class DirectDirectoryDiscoveryIntegrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Multiple_roots_and_an_overlapping_entry_survive_reopened_database_without_duplicate_results(int maxDepth)
    {
        using var workspace = new TestWorkspace();
        var rootA = Path.Combine(workspace.SourcePath, "A");
        var rootB = Path.Combine(workspace.SourcePath, "B");
        var nestedRoot = Path.Combine(rootA, "nested");
        var nestedChild = Path.Combine(nestedRoot, "child");
        var otherChild = Path.Combine(rootB, "other");
        Directory.CreateDirectory(nestedChild);
        Directory.CreateDirectory(otherChild);
        string[] roots = [rootA, rootB, nestedRoot];
        string[] expected = [rootA, rootB, nestedRoot, nestedChild, otherChild];
        var request = new CatalogRequest("multiple-roots", rootA, workspace.DatabasePath) { EntryPaths = roots };
        Dictionary<string, Guid> publishedScanIds;

        await using (var provider = CreateMultiRootProvider())
        {
            var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(request);
            Assert.True(File.Exists(workspace.DatabasePath));
            Assert.Equal(5, result.CandidateCount);
            Assert.Equal(5, result.MatchedCount);
            Assert.Equal(expected.Order(), result.Items.Select(static item => item.FullPath).Order());
            Assert.Equal(3, result.ScanReports.Count);
            Assert.All(result.ScanReports, static report => Assert.Equal(
                global::FindEverything.Application.Indexing.IndexScanStatus.Completed, report.Status));
            publishedScanIds = result.ScanReports.ToDictionary(static report => report.RootPath, static report => report.ScanId);

            await using var session = provider.GetRequiredService<global::FindEverything.Application.Indexing.IIndexSessionFactory>()
                .Create(workspace.DatabasePath);
            foreach (var root in roots)
            {
                var status = await session.GetRootStatusAsync(root);
                Assert.Equal(global::FindEverything.Application.Indexing.IndexRootAvailability.Available, status.Availability);
                Assert.Equal(publishedScanIds[root], status.LastScanId);
            }
        }

        // A fresh host and fresh engine sessions prove that no live accumulator,
        // retained model, or in-memory root metadata is required for loading.
        await using (var reopened = CreateMultiRootProvider())
        {
            var loaded = await reopened.GetRequiredService<ICatalogService>().LoadExistingAsync(request);
            Assert.Null(loaded.ScanReport);
            Assert.Empty(loaded.ScanReports);
            Assert.Equal(5, loaded.CandidateCount);
            Assert.Equal(5, loaded.MatchedCount);
            Assert.Equal(expected.Order(), loaded.Items.Select(static item => item.FullPath).Order());
            Assert.False(loaded.HasPendingScopes);
            await using var session = reopened.GetRequiredService<global::FindEverything.Application.Indexing.IIndexSessionFactory>()
                .Create(workspace.DatabasePath);
            foreach (var root in roots)
            {
                var status = await session.GetRootStatusAsync(root);
                Assert.Equal(global::FindEverything.Application.Indexing.IndexRootAvailability.Available, status.Availability);
                Assert.Equal(publishedScanIds[root], status.LastScanId);
            }
        }

        ServiceProvider CreateMultiRootProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
            services.AddFindEverythingApplication(new ConfigurationBuilder().Build())
                .AddFindEverythingInfrastructure().AddProfileRuntime();
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateOnBuild = true, ValidateScopes = true });
            var review = provider.GetRequiredService<IProfileDefinitionCompiler>().Validate(new()
            {
                ContractVersion = ProfileContract.CurrentMajor,
                Kind = ProfileKind.Declarative,
                Id = "multiple-roots",
                Version = "1",
                DisplayName = "Multiple roots",
                MaxScanDepth = maxDepth,
                Fields = [new() { FieldId = "name", GroupName = "name", Header = "Name", Required = true }],
                Rules = [new()
                {
                    Id = "all",
                    Pattern = Regex.Escape(workspace.SourcePath + Path.DirectorySeparatorChar) + "(?<name>.+)",
                    MatchMode = ProfileRegexMatchMode.Full,
                }],
            });
            Assert.True(review.IsValid, string.Join(Environment.NewLine, review.Diagnostics.Select(static value => value.Message)));
            provider.GetRequiredService<IProfileCatalogPublisher>().Publish(new([review.Profile!], [], DateTimeOffset.UtcNow));
            return provider;
        }
    }

    [Fact]
    public async Task Required_missing_text_keeps_the_matched_row_and_reports_a_separate_field_issue_count()
    {
        using var workspace = new TestWorkspace();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build())
            .AddFindEverythingInfrastructure().AddProfileRuntime();
        await using var provider = services.BuildServiceProvider();
        var review = provider.GetRequiredService<IProfileDefinitionCompiler>().Validate(new()
        {
            ContractVersion = ProfileContract.CurrentMajor, Kind = ProfileKind.Declarative,
            Id = "missing-text", Version = "1", DisplayName = "Missing text", MaxScanDepth = 0,
            TextFileFields = [new() { FieldId = "notes", Header = "Notes", Required = true, FileNamePattern = "notes\\.txt" }],
            Rules = [new() { Id = "root", Pattern = Regex.Escape(workspace.SourcePath) }],
        });
        Assert.True(review.IsValid);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(new([review.Profile!], [], DateTimeOffset.UtcNow));
        var result = await provider.GetRequiredService<ICatalogService>().ScanAndLoadAsync(
            new("missing-text", workspace.SourcePath, workspace.DatabasePath));
        Assert.Equal(1, result.MatchedCount); Assert.Equal(1, result.FieldIssueRowCount);
        Assert.Equal(0, result.InvalidCount); Assert.Empty(result.InvalidItems);
        var row = Assert.Single(result.Items);
        Assert.Equal(ProfileFieldResourceState.Missing, row.Resources["notes"].State);
        Assert.NotEmpty(row.FieldErrors["notes"]);
    }

    [Fact]
    public async Task ScanAndLoad_writes_the_selected_database_and_the_same_root_loads_again()
    {
        using var workspace = new TestWorkspace();
        var projectPath = Path.Combine(workspace.SourcePath, "2026", "0521_Project");
        var siblingPath = Path.Combine(workspace.SourcePath, "2026", "0522_Sibling");
        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(siblingPath);

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
        services
            .AddFindEverythingApplication(configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(CreateManifest(workspace.SourcePath));
        Assert.True(
            review.IsValid,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var profile = Assert.IsAssignableFrom<ILoadedProfile>(review.Profile);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));

        var request = new CatalogRequest(
            profile.Descriptor.Id,
            workspace.SourcePath,
            workspace.DatabasePath);
        var service = provider.GetRequiredService<ICatalogService>();

        var indexed = await service.ScanAndLoadAsync(request);

        Assert.True(File.Exists(workspace.DatabasePath));
        Assert.Equal(
            global::FindEverything.Application.Indexing.IndexScanStatus.Completed,
            Assert.IsType<global::FindEverything.Application.Indexing.IndexScanReport>(
                indexed.ScanReport).Status);
        Assert.Equal(
            [projectPath, siblingPath],
            indexed.Items.Select(static item => item.FullPath).Order().ToArray());

        var loaded = await service.LoadExistingAsync(request);

        Assert.Null(loaded.ScanReport);
        Assert.Equal(
            indexed.Items.Select(static item => item.FullPath).Order(),
            loaded.Items.Select(static item => item.FullPath).Order());
    }

    [Fact]
    public async Task ScanAndLoad_excludes_the_matching_directory_row_and_subtree_but_not_the_selected_root()
    {
        using var workspace = new TestWorkspace();
        var scanRoot = Path.Combine(workspace.SourcePath, "name");
        var excludedPath = Path.Combine(scanRoot, "name");
        var hiddenMatchPath = Path.Combine(excludedPath, "2026", "0521_Hidden");
        var visibleMatchPath = Path.Combine(scanRoot, "2026", "0522_Visible");
        Directory.CreateDirectory(hiddenMatchPath);
        Directory.CreateDirectory(visibleMatchPath);

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
        var trace = new RecordingScanTraceSink();
        services.AddSingleton<ICatalogScanTraceSink>(trace);
        services
            .AddFindEverythingApplication(configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var manifest = CreateManifest(scanRoot);
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "skip-name",
                Pattern = "name",
                MatchMode = ProfileRegexMatchMode.Full,
                TimeoutMilliseconds = 100,
            },
        ];
        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);
        Assert.True(
            review.IsValid,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var profile = Assert.IsAssignableFrom<ILoadedProfile>(review.Profile);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));
        var request = new CatalogRequest(
            profile.Descriptor.Id,
            scanRoot,
            workspace.DatabasePath);
        var service = provider.GetRequiredService<ICatalogService>();

        var indexed = await service.ScanAndLoadAsync(request);

        Assert.Equal(3, indexed.CandidateCount);
        Assert.Equal(1, indexed.ExcludedDirectoryCount);
        Assert.Equal(visibleMatchPath, Assert.Single(indexed.Items).FullPath);
        Assert.DoesNotContain(indexed.Items, item => item.FullPath == hiddenMatchPath);
        var excludedTrace = Assert.Single(
            trace.Events,
            static entry => entry.Kind == CatalogScanTraceKind.DirectoryExcluded);
        Assert.Equal(excludedPath, excludedTrace.FullPath);
        Assert.Equal("skip-name", excludedTrace.MatchedRuleId);
        Assert.Equal(
            global::FindEverything.Application.Indexing.DirectoryTraversalDecision.ExcludeSubtree,
            excludedTrace.TraversalDecision);

        var loaded = await service.LoadExistingAsync(request);

        // Existing-index loads synthesize the root and map every directory row. Matching these
        // candidate counts proves the excluded directory itself was not published to SQLite.
        Assert.Equal(indexed.CandidateCount, loaded.CandidateCount);
        Assert.Equal(visibleMatchPath, Assert.Single(loaded.Items).FullPath);
    }

    [Fact]
    public async Task Discover_maps_terminal_directories_prunes_children_and_does_not_create_an_index()
    {
        using var workspace = new TestWorkspace();
        var projectPath = Path.Combine(workspace.SourcePath, "2026", "0521_Project");
        var childPath = Path.Combine(projectPath, "child");
        var siblingPath = Path.Combine(workspace.SourcePath, "2026", "0522_Sibling");
        Directory.CreateDirectory(childPath);
        Directory.CreateDirectory(siblingPath);

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
        services
            .AddFindEverythingApplication(configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(CreateManifest(workspace.SourcePath));
        Assert.True(
            review.IsValid,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var profile = Assert.IsAssignableFrom<ILoadedProfile>(review.Profile);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));

        var result = await provider.GetRequiredService<ICatalogService>().DiscoverAsync(
            new CatalogRequest(profile.Descriptor.Id, workspace.SourcePath, workspace.DatabasePath));

        Assert.Equal(4, result.CandidateCount);
        Assert.Equal(2, result.NoMatchCount);
        Assert.Empty(result.InvalidItems);
        Assert.Equal(2, result.Items.Count);
        var project = Assert.Single(result.Items, item => item.FullPath == projectPath);
        Assert.Equal(new DateTime(2026, 5, 21), project.Values["captured-on"]);
        Assert.Equal("Project", project.Values["name"]);
        var sibling = Assert.Single(result.Items, item => item.FullPath == siblingPath);
        Assert.Equal(new DateTime(2026, 5, 22), sibling.Values["captured-on"]);
        Assert.Equal("Sibling", sibling.Values["name"]);
        Assert.DoesNotContain(result.Items, item => item.FullPath == childPath);

        var discovery = Assert.IsType<global::FindEverything.Application.Indexing.DirectoryDiscoveryReport>(
            result.DiscoveryReport);
        Assert.Equal(
            global::FindEverything.Application.Indexing.DirectoryDiscoveryStatus.Completed,
            discovery.Status);
        Assert.Equal(4, discovery.Progress.Directories);
        Assert.Equal(2, discovery.Progress.PrunedDirectories);
        Assert.Empty(discovery.Errors);
        Assert.Null(result.ScanReport);
        Assert.False(File.Exists(workspace.DatabasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    [Fact]
    public async Task Discover_excludes_a_matching_leaf_directory_and_descendants_but_continues_with_siblings()
    {
        using var workspace = new TestWorkspace();
        var excludedPath = Path.Combine(workspace.SourcePath, "name");
        var hiddenMatchPath = Path.Combine(excludedPath, "2026", "0521_Hidden");
        var visibleMatchPath = Path.Combine(workspace.SourcePath, "2026", "0522_Visible");
        Directory.CreateDirectory(hiddenMatchPath);
        Directory.CreateDirectory(visibleMatchPath);

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
        var trace = new RecordingScanTraceSink();
        services.AddSingleton<ICatalogScanTraceSink>(trace);
        services
            .AddFindEverythingApplication(configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var manifest = CreateManifest(workspace.SourcePath);
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "skip-name",
                Pattern = "name",
                MatchMode = ProfileRegexMatchMode.Full,
                TimeoutMilliseconds = 100,
            },
        ];
        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);
        Assert.True(
            review.IsValid,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var profile = Assert.IsAssignableFrom<ILoadedProfile>(review.Profile);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));

        var result = await provider.GetRequiredService<ICatalogService>().DiscoverAsync(
            new CatalogRequest(profile.Descriptor.Id, workspace.SourcePath, workspace.DatabasePath));

        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(2, result.NoMatchCount);
        Assert.Equal(1, result.ExcludedDirectoryCount);
        Assert.Empty(result.DirectoryExclusionIssues);
        Assert.Empty(result.InvalidItems);
        var visible = Assert.Single(result.Items);
        Assert.Equal(visibleMatchPath, visible.FullPath);
        Assert.DoesNotContain(result.Items, item => item.FullPath == hiddenMatchPath);

        var discovery = Assert.IsType<global::FindEverything.Application.Indexing.DirectoryDiscoveryReport>(
            result.DiscoveryReport);
        Assert.Equal(4, discovery.Progress.Directories);
        Assert.Equal(2, discovery.Progress.PrunedDirectories);
        Assert.Empty(discovery.Errors);
        var excludedTrace = Assert.Single(
            trace.Events,
            static entry => entry.Kind == CatalogScanTraceKind.DirectoryExcluded);
        Assert.Equal(excludedPath, excludedTrace.FullPath);
        Assert.Equal("name", excludedTrace.MatchInput);
        Assert.Equal("skip-name", excludedTrace.MatchedRuleId);
        Assert.Equal(
            global::FindEverything.Application.Indexing.DirectoryTraversalDecision.ExcludeSubtree,
            excludedTrace.TraversalDecision);
    }

    private static ProfileManifest CreateManifest(string rootPath) =>
        new()
        {
            ContractVersion = ProfileContract.CurrentMajor,
            Kind = ProfileKind.Declarative,
            Id = "direct-discovery",
            Version = "1.0.0",
            DisplayName = "Direct discovery",
            CandidateKind = ProfileCandidateKind.Directory,
            Fields =
            [
                new ProfileFieldManifest
                {
                    FieldId = "captured-on",
                    GroupNames = ["year", "monthDay"],
                    Header = "Date",
                    Order = 10,
                    Required = true,
                    Kind = ProfileFieldValueKind.DateTime,
                    ParseFormat = "yyyyMMdd",
                    DisplayFormat = "yyyy-MM-dd",
                },
                new ProfileFieldManifest
                {
                    FieldId = "name",
                    GroupName = "name",
                    Header = "Name",
                    Order = 20,
                    Required = true,
                    Kind = ProfileFieldValueKind.String,
                },
            ],
            Rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "default",
                    Pattern = string.Concat(
                        Regex.Escape(Path.GetFullPath(rootPath) + Path.DirectorySeparatorChar),
                        @"(?<year>\d{4})",
                        Regex.Escape(Path.DirectorySeparatorChar.ToString()),
                        @"(?<monthDay>\d{4})_(?<name>[^\\/]+)"),
                    MatchMode = ProfileRegexMatchMode.Full,
                    TimeoutMilliseconds = 100,
                    StopTraversalWhenCapturedGroups = ["year", "monthDay"],
                },
            ],
        };

    private sealed class NoOpLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class RecordingScanTraceSink : ICatalogScanTraceSink
    {
        private readonly List<CatalogScanTraceEvent> _events = [];

        public IReadOnlyList<CatalogScanTraceEvent> Events => _events;

        public void Report(CatalogScanTraceEvent value) => _events.Add(value);
    }
}
