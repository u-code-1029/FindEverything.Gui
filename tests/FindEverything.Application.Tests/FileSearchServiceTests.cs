using FindEverything.Application.FileSearch;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class FileSearchServiceTests
{
    [Fact]
    public async Task Scan_UsesLatestValidatedIndexingSettings()
    {
        var factory = new RecordingSessionFactory();
        await using var provider = CreateProvider(factory);
        provider.GetRequiredService<IValidatedSettingsUpdater<IndexingOptions>>().Publish(new IndexingOptions
        {
            MaxEntriesPerSecond = 3456,
            DirectoryDelayMilliseconds = 27,
            SearchPageSize = 125
        });
        var scope = Scope();

        await provider.GetRequiredService<IFileSearchService>().ScanAsync(scope);

        var request = Assert.IsType<IndexScanRequest>(factory.Session.ScanRequest);
        Assert.Equal(scope.RootPath, request.RootPath);
        Assert.Equal(3456, request.Options.MaxEntriesPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(27), request.Options.DirectoryDelay);
        Assert.Equal(scope.DatabasePath, factory.LastDatabasePath);
    }

    [Fact]
    public async Task Search_ForwardsAllServerSideFiltersAndResultMetadata()
    {
        var factory = new RecordingSessionFactory();
        var expectedEntry = new IndexedPathEntry(
            Path.Combine(Path.GetTempPath(), "source", "result.txt"),
            "result.txt",
            Path.Combine(Path.GetTempPath(), "source"),
            IndexedPathKind.File,
            42,
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(-1),
            true);
        factory.Session.SearchResult = new EntrySearchResult([expectedEntry], 17, true, true);
        await using var provider = CreateProvider(factory);
        var scope = Scope();
        var request = new EntrySearchRequest(scope.RootPath)
        {
            Keyword = "report final",
            Kind = IndexedPathKind.File,
            MinSizeBytes = 10,
            MaxSizeBytes = 100,
            CreatedFromUtc = DateTimeOffset.UtcNow.AddMonths(-1),
            CreatedBeforeUtc = DateTimeOffset.UtcNow,
            ModifiedFromUtc = DateTimeOffset.UtcNow.AddDays(-7),
            ModifiedBeforeUtc = DateTimeOffset.UtcNow.AddDays(1),
            SortBy = EntrySortField.Modified,
            SortDirection = EntrySortDirection.Descending,
            Limit = 250,
            Offset = 500
        };

        var result = await provider.GetRequiredService<IFileSearchService>().SearchAsync(scope, request);

        Assert.Same(request, factory.Session.EntrySearchRequest);
        Assert.Same(factory.Session.SearchResult, result);
        Assert.Equal(17, result.TotalCount);
        Assert.True(result.HasMore);
        Assert.True(result.HasPendingScopes);
        Assert.Same(expectedEntry, Assert.Single(result.Entries));
        Assert.Equal(scope.DatabasePath, factory.LastDatabasePath);
    }

    [Fact]
    public async Task Operations_UseTheSameRootAndDatabase()
    {
        var factory = new RecordingSessionFactory();
        await using var provider = CreateProvider(factory);
        var service = provider.GetRequiredService<IFileSearchService>();
        var scope = Scope();

        await service.ScanAsync(scope);
        await service.SearchAsync(scope, new EntrySearchRequest(scope.RootPath));
        var status = await service.GetStatusAsync(scope);

        Assert.Equal(
            new[] { scope.DatabasePath, scope.DatabasePath, scope.DatabasePath },
            factory.DatabasePaths);
        Assert.Equal(scope.RootPath, factory.Session.ScanRequest?.RootPath);
        Assert.Equal(scope.RootPath, factory.Session.EntrySearchRequest?.RootPath);
        Assert.Equal(scope.RootPath, factory.Session.StatusRootPath);
        Assert.Equal(IndexRootAvailability.Available, status.Availability);
    }

    [Fact]
    public async Task Search_RejectsMismatchedRootBeforeOpeningDatabase()
    {
        var factory = new RecordingSessionFactory();
        await using var provider = CreateProvider(factory);
        var scope = Scope();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.GetRequiredService<IFileSearchService>().SearchAsync(
                scope,
                new EntrySearchRequest(Path.Combine(scope.RootPath, "other"))));

        Assert.Empty(factory.DatabasePaths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Search_RejectsPageSizesOutsideEngineBoundary(int limit)
    {
        var factory = new RecordingSessionFactory();
        await using var provider = CreateProvider(factory);
        var scope = Scope();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.GetRequiredService<IFileSearchService>().SearchAsync(
                scope,
                new EntrySearchRequest(scope.RootPath) { Limit = limit }));

        Assert.Empty(factory.DatabasePaths);
    }

    [Fact]
    public async Task Search_PropagatesCancellation()
    {
        var factory = new RecordingSessionFactory();
        factory.Session.CancelSearch = true;
        await using var provider = CreateProvider(factory);
        var scope = Scope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IFileSearchService>().SearchAsync(
                scope,
                new EntrySearchRequest(scope.RootPath),
                cancellation.Token));
    }

    [Fact]
    public async Task Scan_TranslatesCancelledReportToCancellation()
    {
        var factory = new RecordingSessionFactory();
        factory.Session.ScanStatus = IndexScanStatus.Cancelled;
        await using var provider = CreateProvider(factory);
        var scope = Scope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IFileSearchService>().ScanAsync(scope));
    }

    [Fact]
    public async Task Status_PreservesNoIndexState()
    {
        var factory = new RecordingSessionFactory();
        var scope = Scope();
        factory.Session.StatusResult = new IndexRootStatus(
            scope.RootPath,
            IndexRootAvailability.DatabaseMissing,
            null,
            null,
            null,
            null,
            0,
            0,
            false);
        await using var provider = CreateProvider(factory);

        var status = await provider.GetRequiredService<IFileSearchService>().GetStatusAsync(scope);

        Assert.Same(factory.Session.StatusResult, status);
        Assert.Equal(scope.DatabasePath, factory.LastDatabasePath);
    }

    private static ServiceProvider CreateProvider(IIndexSessionFactory factory)
    {
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(factory);
        return services.BuildServiceProvider();
    }

    private static FileSearchScope Scope()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "file-search-root"));
        return new FileSearchScope(root, Path.Combine(Path.GetTempPath(), "file-search-index.db"));
    }

    private sealed class RecordingSessionFactory : IIndexSessionFactory
    {
        public RecordingSession Session { get; } = new();

        public List<string> DatabasePaths { get; } = [];

        public string? LastDatabasePath => DatabasePaths.LastOrDefault();

        public IIndexSession Create(string databasePath)
        {
            DatabasePaths.Add(databasePath);
            Session.DatabasePath = databasePath;
            return Session;
        }
    }

    private sealed class RecordingSession : IIndexSession
    {
        public string DatabasePath { get; set; } = string.Empty;

        public IndexScanRequest? ScanRequest { get; private set; }

        public EntrySearchRequest? EntrySearchRequest { get; private set; }

        public string? StatusRootPath { get; private set; }

        public bool CancelSearch { get; set; }

        public IndexScanStatus ScanStatus { get; set; } = IndexScanStatus.Completed;

        public EntrySearchResult SearchResult { get; set; } = new([], 0, false, false);

        public IndexRootStatus? StatusResult { get; set; }

        public Task<IndexScanReport> ScanAsync(
            IndexScanRequest request,
            IProgress<IndexScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanRequest = request;
            return Task.FromResult(new IndexScanReport(
                Guid.NewGuid(),
                request.RootPath,
                request.ScopePath ?? request.RootPath,
                ScanStatus,
                new IndexScanProgress(0, 0, 0, 0, 0, TimeSpan.Zero, 0),
                [],
                []));
        }

        public Task<DirectorySearchResult> SearchDirectoriesAsync(
            DirectorySearchRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EntrySearchResult> SearchEntriesAsync(
            EntrySearchRequest request,
            CancellationToken cancellationToken = default)
        {
            EntrySearchRequest = request;
            if (CancelSearch)
                return Task.FromCanceled<EntrySearchResult>(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SearchResult);
        }

        public Task<IndexRootStatus> GetRootStatusAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusRootPath = rootPath;
            return Task.FromResult(StatusResult ?? new IndexRootStatus(
                rootPath,
                IndexRootAvailability.Available,
                Guid.NewGuid(),
                rootPath,
                IndexScanStatus.Completed,
                DateTimeOffset.UtcNow,
                0,
                0,
                false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
