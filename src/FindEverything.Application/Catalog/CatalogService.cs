using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Profile.Runtime;
using System.Collections.ObjectModel;

namespace FindEverything.Application.Catalog;

internal sealed class CatalogService(
    IIndexSessionFactory sessionFactory,
    IDirectoryDiscoveryService directoryDiscovery,
    IProfileResolver profileResolver,
    IValidatedSettingsState<IndexingOptions> indexingSettings,
    ICatalogScanTraceSink scanTraceSink,
    AbsoluteProfilePathCanonicalizer absolutePathCanonicalizer,
    IProfilePathCanonicalizer pathCanonicalizer) : ICatalogService, IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public Task<CatalogResult> LoadExistingAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            (normalizedRequest, profileRootPath, profile, token) =>
                LoadFromIndexAsync(
                    normalizedRequest,
                    profileRootPath,
                    profile,
                    progress,
                    token),
            requireDatabase: true,
            cancellationToken);

    public Task<CatalogResult> DiscoverAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            (normalizedRequest, profileRootPath, profile, token) =>
                DiscoverDirectoriesAsync(
                    normalizedRequest,
                    profileRootPath,
                    profile,
                    progress,
                    token),
            requireDatabase: false,
            cancellationToken);

    public Task<CatalogResult> ScanAndLoadAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            (normalizedRequest, profileRootPath, profile, token) =>
                ScanAndLoadFromIndexAsync(
                    normalizedRequest,
                    profileRootPath,
                    profile,
                    progress,
                    token),
            requireDatabase: true,
            cancellationToken);

    private async Task<CatalogResult> ExecuteLockedAsync(
        CatalogRequest request,
        Func<CatalogRequest, string, ILoadedProfile, CancellationToken, Task<CatalogResult>> operation,
        bool requireDatabase,
        CancellationToken cancellationToken)
    {
        Validate(request, requireDatabase);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!profileResolver.TryResolve(request.ProfileId, out var profile))
                throw new InvalidOperationException($"Profile '{request.ProfileId}' is not loaded.");

            var scanRootPath = absolutePathCanonicalizer.Canonicalize(request.RootPath);
            var profileRootPath = pathCanonicalizer.Canonicalize(scanRootPath);
            var normalizedRequest = request with
            {
                RootPath = scanRootPath,
            };
            return await operation(
                    normalizedRequest,
                    profileRootPath,
                    profile,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<CatalogResult> DiscoverDirectoriesAsync(
        CatalogRequest request,
        string profileRootPath,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var trace = new ScanTraceWriter(
            scanTraceSink,
            request,
            profile.Descriptor);
        trace.Report(
            CatalogScanTraceKind.Started,
            message: "프로필 기반 빠른 스캔을 시작했습니다.");

        try
        {
            progress?.Report(new(
                CatalogOperationPhase.Preparing,
                "프로필 기반 빠른 스캔을 준비하고 있습니다."));

            var accumulator = new MappingAccumulator(
                profile,
                request.RootPath,
                profileRootPath,
                pathCanonicalizer,
                progress,
                trace);
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

            foreach (var error in report.Errors)
            {
                trace.ReportDiscoveryError(error);
            }

            if (report.Status == DirectoryDiscoveryStatus.Cancelled)
                throw new OperationCanceledException(cancellationToken);

            var result = accumulator.BuildResult(hasPendingScopes: false, scanReport: null) with
            {
                DiscoveryReport = report,
            };
            trace.Report(
                CatalogScanTraceKind.Completed,
                message: CreateCompletionMessage(result, report));
            return result;
        }
        catch (OperationCanceledException)
        {
            trace.Report(
                CatalogScanTraceKind.Cancelled,
                message: "빠른 스캔이 취소되었습니다.");
            throw;
        }
        catch (Exception exception)
        {
            trace.Report(
                CatalogScanTraceKind.Failed,
                message: $"빠른 스캔에 실패했습니다: {exception.Message}");
            throw;
        }
    }

    private async Task<CatalogResult> LoadFromIndexAsync(
        CatalogRequest request,
        string profileRootPath,
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
            profileRootPath,
            profile,
            session,
            operationSettings,
            progress,
            scanReport: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogResult> ScanAndLoadFromIndexAsync(
        CatalogRequest request,
        string profileRootPath,
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
            profileRootPath,
            profile,
            session,
            operationSettings,
            progress,
            scanReport,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogResult> LoadFromIndexSessionAsync(
        CatalogRequest request,
        string profileRootPath,
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

        var accumulator = new MappingAccumulator(
            profile,
            request.RootPath,
            profileRootPath,
            pathCanonicalizer,
            progress);
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

    private static string CreateCompletionMessage(
        CatalogResult result,
        DirectoryDiscoveryReport report)
    {
        var errorSummary = report.Progress.ErrorCount == report.Errors.Count
            ? $"오류 {report.Progress.ErrorCount:N0}개"
            : $"오류 {report.Progress.ErrorCount:N0}개(상세 {report.Errors.Count:N0}개 기록)";
        return $"빠른 스캔을 완료했습니다. 폴더 {result.CandidateCount:N0}개, 일치 {result.Items.Count:N0}개, {errorSummary}를 확인했습니다.";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MappingAccumulator(
        ILoadedProfile profile,
        string rootPath,
        string profileRootPath,
        IProfilePathCanonicalizer pathCanonicalizer,
        IProgress<CatalogOperationProgress>? progress,
        ScanTraceWriter? trace = null)
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
            var absolutePath = ResolveProfileInputPath(directory.FullPath, relativePath);
            var mapping = profile.Map(new ProfilePathCandidate(absolutePath));
            _candidateCount++;
            switch (mapping.Status)
            {
                case ProfileMapStatus.Success when mapping.Item is not null:
                    _items.Add(new CatalogItem(
                        mapping.Item.FullPath,
                        relativePath,
                        mapping.Item.MatchedRuleId,
                        mapping.Item.Model,
                        mapping.Item.Values,
                        coveragePending));
                    break;
                case ProfileMapStatus.Invalid:
                    _invalidItems.Add(new CatalogInvalidItem(
                        absolutePath,
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

            var traversalDecision = mapping.ShouldPruneDescendants
                ? DirectoryTraversalDecision.SkipDescendants
                : DirectoryTraversalDecision.Continue;
            trace?.ReportDirectoryVisited(
                directory.FullPath,
                absolutePath,
                relativePath,
                mapping,
                traversalDecision);
            return traversalDecision;
        }

        private string ResolveProfileInputPath(string discoveredPath, string relativePath)
        {
            if (relativePath == ".")
            {
                return profileRootPath;
            }

            if (!Path.IsPathFullyQualified(relativePath)
                && !relativePath.Equals("..", StringComparison.Ordinal)
                && !relativePath.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                && !relativePath.StartsWith(
                    ".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return Path.GetFullPath(Path.Combine(profileRootPath, relativePath));
            }

            return pathCanonicalizer.Canonicalize(discoveredPath);
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

    private sealed class ScanTraceWriter(
        ICatalogScanTraceSink sink,
        CatalogRequest request,
        ProfileDescriptor profile)
    {
        private static readonly IReadOnlyDictionary<string, object?> EmptyValues =
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
        private static readonly IReadOnlyList<ProfileMappingIssue> EmptyIssues =
            Array.Empty<ProfileMappingIssue>();
        private readonly Guid _operationId = Guid.NewGuid();
        private long _sequence;

        public void ReportDirectoryVisited(
            string discoveredPath,
            string absolutePath,
            string relativePath,
            ProfileMapResult mapping,
            DirectoryTraversalDecision traversalDecision)
        {
            try
            {
                var values = mapping.Item is null
                    ? EmptyValues
                    : new ReadOnlyDictionary<string, object?>(
                        new Dictionary<string, object?>(mapping.Item.Values, StringComparer.Ordinal));
                var issues = mapping.Issues.Count == 0
                    ? EmptyIssues
                    : Array.AsReadOnly(mapping.Issues.ToArray());

                ReportCore(new CatalogScanTraceEvent(
                    _operationId,
                    Interlocked.Increment(ref _sequence),
                    DateTimeOffset.UtcNow,
                    CatalogScanTraceKind.DirectoryVisited,
                    profile.Id,
                    profile.DisplayName,
                    request.RootPath,
                    discoveredPath,
                    relativePath,
                    absolutePath,
                    mapping.Status,
                    mapping.MatchedRuleId,
                    values,
                    issues,
                    traversalDecision,
                    GetMappingMessage(mapping.Status, traversalDecision)));
            }
            catch (Exception)
            {
                // Snapshotting diagnostics from a custom profile must not affect
                // the catalog result or the traversal decision.
            }
        }

        public void ReportDiscoveryError(DirectoryDiscoveryError error)
        {
            string? relativePath = null;
            if (!string.IsNullOrWhiteSpace(error.Path))
            {
                try
                {
                    relativePath = Path.GetRelativePath(request.RootPath, error.Path);
                }
                catch (Exception)
                {
                    // Diagnostics must not be able to change the scan result.
                }
            }

            Report(
                CatalogScanTraceKind.DiscoveryError,
                fullPath: error.Path,
                relativePath: relativePath,
                message: error.Message);
        }

        public void Report(
            CatalogScanTraceKind kind,
            string? fullPath = null,
            string? relativePath = null,
            string? message = null) =>
            ReportCore(new CatalogScanTraceEvent(
                _operationId,
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow,
                kind,
                profile.Id,
                profile.DisplayName,
                request.RootPath,
                fullPath,
                relativePath,
                null,
                null,
                null,
                EmptyValues,
                EmptyIssues,
                null,
                message ?? string.Empty));

        private void ReportCore(CatalogScanTraceEvent value)
        {
            try
            {
                sink.Report(value);
            }
            catch (Exception)
            {
                // A diagnostics consumer is observational and must never interrupt a scan.
            }
        }

        private static string GetMappingMessage(
            ProfileMapStatus status,
            DirectoryTraversalDecision traversalDecision)
        {
            var message = status switch
            {
                ProfileMapStatus.Success => "프로필 규칙과 일치했습니다.",
                ProfileMapStatus.Invalid => "규칙과 일치했지만 값을 변환하지 못했습니다.",
                ProfileMapStatus.NoMatch => "프로필 규칙과 일치하지 않았습니다.",
                _ => "프로필 판정을 완료했습니다.",
            };
            return traversalDecision == DirectoryTraversalDecision.SkipDescendants
                ? $"{message} 하위 폴더 탐색을 생략합니다."
                : message;
        }
    }
}
