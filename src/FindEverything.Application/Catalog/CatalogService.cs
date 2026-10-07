using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

internal sealed class CatalogService(
    IIndexSessionFactory sessionFactory,
    IDirectoryDiscoveryService directoryDiscovery,
    IProfileResolver profileResolver,
    IValidatedSettingsState<IndexingOptions> indexingSettings) : ICatalogService, IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public Task<CatalogResult> LoadExistingAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            (profile, token) => LoadFromIndexAsync(request, profile, progress, token),
            requireDatabase: true,
            cancellationToken);

    public Task<CatalogResult> DiscoverAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            (profile, token) => DiscoverDirectoriesAsync(request, profile, progress, token),
            requireDatabase: false,
            cancellationToken);

    public Task<CatalogResult> ScanAndLoadAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            (profile, token) => ScanAndLoadFromIndexAsync(request, profile, progress, token),
            requireDatabase: true,
            cancellationToken);

    private async Task<CatalogResult> ExecuteLockedAsync(
        CatalogRequest request,
        Func<ILoadedProfile, CancellationToken, Task<CatalogResult>> operation,
        bool requireDatabase,
        CancellationToken cancellationToken)
    {
        Validate(request, requireDatabase);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!profileResolver.TryResolve(request.ProfileId, out var profile))
                throw new InvalidOperationException($"Profile '{request.ProfileId}' is not loaded.");

            return await operation(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<CatalogResult> DiscoverDirectoriesAsync(
        CatalogRequest request,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            CatalogOperationPhase.Preparing,
            "프로필 기반 빠른 스캔을 준비하고 있습니다."));

        var accumulator = new MappingAccumulator(profile, request.RootPath, progress);
        var operationSettings = indexingSettings.Current;
        var discoveryProgress = progress is null
            ? null
            : new InlineProgress<DirectoryDiscoveryProgress>(value => progress.Report(new(
                CatalogOperationPhase.Scanning,
                $"폴더 {value.Directories:N0}개를 확인하고 하위 탐색 {value.PrunedDirectories:N0}개를 생략했습니다.",
                value.Directories)));

        var report = await directoryDiscovery.DiscoverAsync(
            new DirectoryDiscoveryRequest(request.RootPath)
            {
                MaxEntriesPerSecond = operationSettings.MaxEntriesPerSecond,
                DirectoryDelay = TimeSpan.FromMilliseconds(
                    operationSettings.DirectoryDelayMilliseconds),
            },
            accumulator.Visit,
            discoveryProgress,
            cancellationToken).ConfigureAwait(false);

        if (report.Status == DirectoryDiscoveryStatus.Cancelled)
            throw new OperationCanceledException(cancellationToken);

        return accumulator.BuildResult(hasPendingScopes: false, scanReport: null) with
        {
            DiscoveryReport = report,
        };
    }

    private async Task<CatalogResult> LoadFromIndexAsync(
        CatalogRequest request,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            CatalogOperationPhase.Preparing,
            "프로필과 기존 인덱스를 준비하고 있습니다."));

        var operationSettings = indexingSettings.Current;
        await using var session = sessionFactory.Create(request.DatabasePath);
        return await LoadFromIndexSessionAsync(
            request,
            profile,
            session,
            operationSettings,
            progress,
            scanReport: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogResult> ScanAndLoadFromIndexAsync(
        CatalogRequest request,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            CatalogOperationPhase.Preparing,
            "프로필과 인덱스를 준비하고 있습니다."));

        var operationSettings = indexingSettings.Current;
        await using var session = sessionFactory.Create(request.DatabasePath);
        var scanProgress = progress is null
            ? null
            : new InlineProgress<IndexScanProgress>(value => progress.Report(new(
                CatalogOperationPhase.Scanning,
                $"폴더 {value.Directories:N0}개, 항목 {value.Entries:N0}개를 확인했습니다.",
                value.Entries,
                value)));
        var scanReport = await session.ScanAsync(
            new IndexScanRequest(request.RootPath)
            {
                Options = new IndexScanOptions
                {
                    MaxEntriesPerSecond = operationSettings.MaxEntriesPerSecond,
                    DirectoryDelay = TimeSpan.FromMilliseconds(
                        operationSettings.DirectoryDelayMilliseconds),
                },
            },
            scanProgress,
            cancellationToken).ConfigureAwait(false);

        if (scanReport.Status == IndexScanStatus.Cancelled)
            throw new OperationCanceledException(cancellationToken);

        return await LoadFromIndexSessionAsync(
            request,
            profile,
            session,
            operationSettings,
            progress,
            scanReport,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CatalogResult> LoadFromIndexSessionAsync(
        CatalogRequest request,
        ILoadedProfile profile,
        IIndexSession session,
        IndexingOptions operationSettings,
        IProgress<CatalogOperationProgress>? progress,
        IndexScanReport? scanReport,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(CatalogOperationPhase.Searching, "인덱스에서 폴더를 조회하고 있습니다."));
        var searchResult = await session.SearchDirectoriesAsync(
            new DirectorySearchRequest(request.RootPath)
            {
                PageSize = operationSettings.SearchPageSize,
            },
            cancellationToken).ConfigureAwait(false);

        var accumulator = new MappingAccumulator(profile, request.RootPath, progress);
        foreach (var directory in searchResult.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Visit(new DiscoveredDirectory(
                    directory.FullPath,
                    directory.Name,
                    directory.ParentPath,
                    directory.CreatedUtc,
                    directory.ModifiedUtc),
                directory.CoveragePending);
        }

        return accumulator.BuildResult(
            searchResult.HasPendingScopes
                || searchResult.Directories.Any(static directory => directory.CoveragePending),
            scanReport);
    }

    private static void Validate(CatalogRequest request, bool requireDatabase)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        if (requireDatabase)
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
    }

    public void Dispose() => _operationGate.Dispose();

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MappingAccumulator(
        ILoadedProfile profile,
        string rootPath,
        IProgress<CatalogOperationProgress>? progress)
    {
        private readonly List<CatalogItem> _items = [];
        private readonly List<CatalogInvalidItem> _invalidItems = [];
        private int _candidateCount;
        private int _noMatchCount;

        public DirectoryTraversalDecision Visit(DiscoveredDirectory directory) =>
            Visit(directory, coveragePending: false);

        public DirectoryTraversalDecision Visit(
            DiscoveredDirectory directory,
            bool coveragePending)
        {
            var relativePath = Path.GetRelativePath(rootPath, directory.FullPath);
            var mapping = profile.Map(new ProfilePathCandidate(directory.FullPath, relativePath));
            _candidateCount++;
            switch (mapping.Status)
            {
                case ProfileMapStatus.Success when mapping.Item is not null:
                    _items.Add(new CatalogItem(
                        mapping.Item.FullPath,
                        mapping.Item.RelativePath,
                        mapping.Item.MatchedRuleId,
                        mapping.Item.Model,
                        mapping.Item.Values,
                        coveragePending));
                    break;
                case ProfileMapStatus.Invalid:
                    _invalidItems.Add(new CatalogInvalidItem(
                        directory.FullPath,
                        relativePath,
                        mapping.Issues));
                    break;
                case ProfileMapStatus.NoMatch:
                    _noMatchCount++;
                    break;
            }

            if (_candidateCount == 1 || _candidateCount % 250 == 0)
            {
                progress?.Report(new(
                    CatalogOperationPhase.Mapping,
                    $"경로 {_candidateCount:N0}개를 프로필 규칙으로 판별했습니다.",
                    _candidateCount));
            }

            return mapping.ShouldPruneDescendants
                ? DirectoryTraversalDecision.SkipDescendants
                : DirectoryTraversalDecision.Continue;
        }

        public CatalogResult BuildResult(bool hasPendingScopes, IndexScanReport? scanReport) =>
            new(
                profile.Descriptor,
                _items.AsReadOnly(),
                _invalidItems.AsReadOnly(),
                _candidateCount,
                _noMatchCount,
                hasPendingScopes,
                scanReport);
    }
}
