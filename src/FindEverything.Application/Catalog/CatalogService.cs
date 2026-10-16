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
            CatalogOperation.Load,
            progress,
            requireDatabase: true,
            cancellationToken);

    public Task<CatalogResult> DiscoverAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            CatalogOperation.Discover,
            progress,
            requireDatabase: false,
            cancellationToken);

    public Task<CatalogResult> ScanAndLoadAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            request,
            CatalogOperation.Scan,
            progress,
            requireDatabase: true,
            cancellationToken);

    private async Task<CatalogResult> ExecuteLockedAsync(
        CatalogRequest request,
        CatalogOperation operation,
        IProgress<CatalogOperationProgress>? progress,
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

            // Preserve explicit descendant roots: depth limits and root-name exclusions
            // are relative to each starting path, not to their common ancestor.
            var roots = NormalizeEntryPaths(request);
            for (var attempt = 1; ; attempt++)
            {
                var state = new MappingState();
                var scanReports = new List<IndexScanReport>();
                var discoveryReports = new List<DirectoryDiscoveryReport>();
                CatalogResult? result = null;
                try
                {
                    for (var index = 0; index < roots.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (scanRootPath, profileRootPath) = roots[index];
                        var normalizedRequest = request with { RootPath = scanRootPath };
                        var entryIndex = index + 1;
                        var completedProgress = SumScanProgress(scanReports);
                        state.Trace.IsLastRoot = index == roots.Count - 1;
                        state.CompletedScanProgress = completedProgress;
                        state.CompletedDiscoveryProgress = AggregateDiscoveryReports(discoveryReports)?.Progress
                            ?? new DirectoryDiscoveryProgress(0, 0, 0, 0, 0, TimeSpan.Zero);
                        var rootProgress = progress is null ? null
                            : new InlineProgress<CatalogOperationProgress>(value =>
                            {
                                var scanProgress = value.ScanProgress is null ? null
                                    : AddScanProgress(completedProgress, value.ScanProgress);
                                progress.Report(value with
                                {
                                    EntryIndex = entryIndex,
                                    EntryCount = roots.Count,
                                    EntryPath = scanRootPath,
                                    AttemptId = attempt,
                                    MatchedCount = state.MatchedCount,
                                    ScanProgress = scanProgress,
                                    ProcessedItems = scanProgress?.Entries ?? value.ProcessedItems,
                                });
                            });
                        result = operation switch
                        {
                            CatalogOperation.Load => await LoadFromIndexAsync(normalizedRequest, profileRootPath,
                                profile, rootProgress, state, attempt, cancellationToken).ConfigureAwait(false),
                            CatalogOperation.Discover => await DiscoverDirectoriesAsync(normalizedRequest, profileRootPath,
                                profile, rootProgress, state, cancellationToken).ConfigureAwait(false),
                            _ => await ScanAndLoadFromIndexAsync(normalizedRequest, profileRootPath,
                                profile, rootProgress, state, cancellationToken).ConfigureAwait(false),
                        };
                        if (result.ScanReport is { } scanReport) scanReports.Add(scanReport);
                        if (result.DiscoveryReport is { } discoveryReport) discoveryReports.Add(discoveryReport);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return result! with
                    {
                        ScanReport = AggregateScanReports(scanReports),
                        ScanReports = scanReports.AsReadOnly(),
                        DiscoveryReport = AggregateDiscoveryReports(discoveryReports),
                        DiscoveryReports = discoveryReports.AsReadOnly(),
                        HasPendingScopes = result.HasPendingScopes
                            || scanReports.Any(static report => report.PendingScopes.Count > 0),
                    };
                }
                catch (IndexSnapshotChangedException) when (operation == CatalogOperation.Load && attempt == 1)
                {
                    // No mixed generation may escape, including rows from preceding roots.
                    cancellationToken.ThrowIfCancellationRequested();
                    request.ResultWriter?.Reset();
                    progress?.Report(new CatalogOperationProgress(CatalogOperationPhase.Preparing,
                        "인덱스가 변경되어 모든 시작 경로의 이전 결과를 지우고 다시 불러옵니다.")
                    { AttemptReset = true, AttemptId = 2, EntryCount = roots.Count });
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private enum CatalogOperation { Load, Discover, Scan }

    private static IndexScanProgress AddScanProgress(IndexScanProgress left, IndexScanProgress right) => new(
        left.Entries + right.Entries, left.Directories + right.Directories,
        left.ExcludedEntries + right.ExcludedEntries, left.SkippedLinks + right.SkippedLinks,
        left.ErrorCount + right.ErrorCount, left.Elapsed + right.Elapsed,
        left.PendingDirectories + right.PendingDirectories);

    private static IndexScanProgress SumScanProgress(IEnumerable<IndexScanReport> reports) =>
        reports.Aggregate(new IndexScanProgress(0, 0, 0, 0, 0, TimeSpan.Zero, 0),
            static (total, report) => AddScanProgress(total, report.Progress));

    private static IndexScanReport? AggregateScanReports(IReadOnlyList<IndexScanReport> reports) => reports.Count switch
    {
        0 => null,
        1 => reports[0],
        // This operation identity is not the scan generation of any individual root.
        _ => new IndexScanReport(Guid.NewGuid(), reports[0].RootPath, reports[0].ScopePath,
            reports.All(static report => report.Status == IndexScanStatus.Completed)
                ? IndexScanStatus.Completed : IndexScanStatus.Partial,
            SumScanProgress(reports), reports.SelectMany(static report => report.Errors).ToArray(),
            reports.SelectMany(static report => report.PendingScopes).ToArray()),
    };

    private static DirectoryDiscoveryReport? AggregateDiscoveryReports(IReadOnlyList<DirectoryDiscoveryReport> reports)
    {
        if (reports.Count <= 1) return reports.FirstOrDefault();
        var total = reports.Aggregate(new DirectoryDiscoveryProgress(0, 0, 0, 0, 0, TimeSpan.Zero),
            static (sum, report) => AddDiscoveryProgress(sum, report.Progress));
        return new DirectoryDiscoveryReport(reports[0].RootPath,
            reports.All(static report => report.Status == DirectoryDiscoveryStatus.Completed)
                ? DirectoryDiscoveryStatus.Completed : DirectoryDiscoveryStatus.Partial,
            total, reports.SelectMany(static report => report.Errors).ToArray());
    }

    private static DirectoryDiscoveryProgress AddDiscoveryProgress(
        DirectoryDiscoveryProgress left, DirectoryDiscoveryProgress right) => new(
        left.Entries + right.Entries, left.Directories + right.Directories,
        left.PrunedDirectories + right.PrunedDirectories, left.SkippedLinks + right.SkippedLinks,
        left.ErrorCount + right.ErrorCount, left.Elapsed + right.Elapsed);

    private IReadOnlyList<(string ScanPath, string ProfilePath)> NormalizeEntryPaths(CatalogRequest request)
    {
        var paths = request.EntryPaths.Count > 0 ? request.EntryPaths : [request.RootPath];
        if (paths.Count > 64)
            throw new ArgumentException("시작 경로는 최대 64개까지 지정할 수 있습니다.", nameof(request));
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var roots = new List<(string, string)>();
        foreach (var path in paths)
        {
            var scanPath = absolutePathCanonicalizer.Canonicalize(path);
            var profilePath = absolutePathCanonicalizer.Canonicalize(pathCanonicalizer.Canonicalize(scanPath));
            if (seen.Add(profilePath)) roots.Add((scanPath, profilePath));
        }
        return roots;
    }

    private async Task<CatalogResult> DiscoverDirectoriesAsync(
        CatalogRequest request,
        string profileRootPath,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        MappingState state,
        CancellationToken cancellationToken)
    {
        var trace = new ScanTraceWriter(
            scanTraceSink,
            request,
            profile.Descriptor,
            state.Trace);
        trace.Report(
            CatalogScanTraceKind.Started,
            message: "DB에 저장하지 않는 바로 스캔을 시작했습니다.");

        try
        {
            progress?.Report(new(
                CatalogOperationPhase.Preparing,
                "DB에 저장하지 않는 바로 스캔을 준비하고 있습니다."));

            var operationSettings = indexingSettings.Current;
            var accumulator = new MappingAccumulator(
                profile,
                state,
                request.RootPath,
                profileRootPath,
                pathCanonicalizer,
                progress,
                applyDirectoryExclusions: true,
                retainItems: request.RetainItems,
                resultWriter: request.ResultWriter,
                trace: trace,
                uiUpdateBatchSize: operationSettings.UiUpdateBatchSize);
            var discoveryProgress = progress is null
                ? null
                : new InlineProgress<DirectoryDiscoveryProgress>(value =>
                {
                    var total = AddDiscoveryProgress(state.CompletedDiscoveryProgress, value);
                    progress.Report(new(CatalogOperationPhase.Scanning,
                        $"폴더 {total.Directories:N0}개를 확인하고 하위 탐색 {total.PrunedDirectories:N0}개를 생략했습니다."
                        + $" · 소요 {CatalogElapsedTimeFormatter.Format(total.Elapsed)}", total.Directories));
                });

            var report = await directoryDiscovery.DiscoverAsync(
                new DirectoryDiscoveryRequest(request.RootPath)
                {
                    MaxDepth = profile.Descriptor.MaxScanDepth,
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
                message: CreateCompletionMessage(result, report with
                { Progress = AddDiscoveryProgress(state.CompletedDiscoveryProgress, report.Progress) }));
            return result;
        }
        catch (OperationCanceledException)
        {
            trace.Report(
                CatalogScanTraceKind.Cancelled,
                message: "바로 스캔이 취소되었습니다.");
            throw;
        }
        catch (Exception exception)
        {
            trace.Report(
                CatalogScanTraceKind.Failed,
                message: $"바로 스캔에 실패했습니다: {exception.Message}");
            throw;
        }
    }

    private async Task<CatalogResult> LoadFromIndexAsync(
        CatalogRequest request,
        string profileRootPath,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        MappingState state,
        int attemptId,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            CatalogOperationPhase.Preparing,
            "프로필과 기존 인덱스를 준비하고 있습니다."));

        var operationSettings = indexingSettings.Current;
        await using var session = sessionFactory.Create(request.DatabasePath);
        var rootStatus = await session.GetRootStatusAsync(
            request.RootPath,
            cancellationToken).ConfigureAwait(false);
        if (rootStatus.Availability != IndexRootAvailability.Available)
        {
            throw new CatalogIndexUnavailableException(
                rootStatus.Availability,
                session.DatabasePath,
                request.RootPath);
        }

        return await LoadFromIndexSessionAttemptAsync(
            request,
            profileRootPath,
            profile,
            session,
            operationSettings,
            progress,
            scanReport: null,
            trace: null,
            cancellationToken,
            state,
            attemptId).ConfigureAwait(false);
    }

    private async Task<CatalogResult> ScanAndLoadFromIndexAsync(
        CatalogRequest request,
        string profileRootPath,
        ILoadedProfile profile,
        IProgress<CatalogOperationProgress>? progress,
        MappingState state,
        CancellationToken cancellationToken)
    {
        var trace = new ScanTraceWriter(
            scanTraceSink,
            request,
            profile.Descriptor,
            state.Trace);
        trace.Report(
            CatalogScanTraceKind.Started,
            message: "스캔한 파일과 폴더 메타데이터를 DB에 저장하는 탐색을 시작했습니다.");

        try
        {
            progress?.Report(new(
                CatalogOperationPhase.Preparing,
                "프로필과 인덱스를 준비하고 있습니다."));

            var operationSettings = indexingSettings.Current;
            await using var session = sessionFactory.Create(request.DatabasePath);
            var accumulator = new MappingAccumulator(
                profile,
                state,
                request.RootPath,
                profileRootPath,
                pathCanonicalizer,
                progress,
                applyDirectoryExclusions: true,
                retainItems: request.RetainItems,
                resultWriter: request.ResultWriter,
                trace: trace,
                uiUpdateBatchSize: operationSettings.UiUpdateBatchSize);
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
                        MaxDepth = profile.Descriptor.MaxScanDepth,
                        MaxEntriesPerSecond = operationSettings.MaxEntriesPerSecond,
                        DirectoryDelay = TimeSpan.FromMilliseconds(
                            operationSettings.DirectoryDelayMilliseconds),
                    },
                    InspectDirectory = directory => accumulator.Visit(
                        directory,
                        directory.CoveragePending),
                },
                scanProgress,
                cancellationToken).ConfigureAwait(false);

            foreach (var error in scanReport.Errors)
            {
                trace.ReportIndexError(error);
            }

            if (scanReport.Status == IndexScanStatus.Cancelled)
                throw new OperationCanceledException(cancellationToken);

            var result = accumulator.BuildResult(
                scanReport.PendingScopes.Count > 0,
                scanReport);
            trace.Report(
                CatalogScanTraceKind.Completed,
                message: CreateCompletionMessage(result, scanReport with
                { Progress = AddScanProgress(state.CompletedScanProgress, scanReport.Progress) }));
            return result;
        }
        catch (OperationCanceledException)
        {
            trace.Report(
                CatalogScanTraceKind.Cancelled,
                message: "DB에 저장하는 스캔이 취소되었습니다.");
            throw;
        }
        catch (Exception exception)
        {
            trace.Report(
                CatalogScanTraceKind.Failed,
                message: $"DB에 저장하는 스캔에 실패했습니다: {exception.Message}");
            throw;
        }
    }

    private async Task<CatalogResult> LoadFromIndexSessionAttemptAsync(
        CatalogRequest request,
        string profileRootPath,
        ILoadedProfile profile,
        IIndexSession session,
        IndexingOptions operationSettings,
        IProgress<CatalogOperationProgress>? progress,
        IndexScanReport? scanReport,
        ScanTraceWriter? trace,
        CancellationToken cancellationToken,
        MappingState state,
        int attemptId = 1)
    {
        var accumulator = new MappingAccumulator(
            profile,
            state,
            request.RootPath,
            profileRootPath,
            pathCanonicalizer,
            progress,
            applyDirectoryExclusions: false,
            retainItems: request.RetainItems,
            resultWriter: request.ResultWriter,
            attemptId: attemptId,
            trace: trace,
            uiUpdateBatchSize: operationSettings.UiUpdateBatchSize);
        // The index stores its root as metadata rather than as an entry row.
        // Synthesize it so an existing-index load evaluates the same path set as
        // live structured scans.
        accumulator.Visit(CreateRootCandidate(request.RootPath));
        progress?.Report(new CatalogOperationProgress(
            CatalogOperationPhase.Searching,
            "인덱스에서 폴더를 조회하고 있습니다.") { AttemptId = attemptId });
        var reportedDirectoryCount = 0;
        ValueTask ConsumePageAsync(
            IReadOnlyList<IndexedDirectory> directories,
            CancellationToken pageCancellationToken)
        {
            foreach (var directory in directories)
            {
                pageCancellationToken.ThrowIfCancellationRequested();
                accumulator.Visit(new DiscoveredDirectory(
                    directory.FullPath,
                    directory.Name,
                    directory.ParentPath,
                    directory.CreatedUtc,
                    directory.ModifiedUtc),
                    directory.CoveragePending);
                reportedDirectoryCount++;
            }

            return ValueTask.CompletedTask;
        }

        var searchResult = await session.SearchDirectoriesAsync(
            new DirectorySearchRequest(request.RootPath)
            {
                PageSize = operationSettings.SearchPageSize,
                IncludeDirectoriesInResult = false,
                PageConsumer = ConsumePageAsync,
            },
            cancellationToken).ConfigureAwait(false);
        // Keep custom/test index providers that predate PageConsumer compatible.
        if (reportedDirectoryCount == 0)
        {
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
        }

        return accumulator.BuildResult(
            searchResult.HasPendingScopes
                || searchResult.Directories.Any(static directory => directory.CoveragePending),
            scanReport);
    }

    private static DiscoveredDirectory CreateRootCandidate(string rootPath)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        var name = Path.GetFileName(trimmedRoot);
        return new DiscoveredDirectory(
            rootPath,
            string.IsNullOrEmpty(name) ? rootPath : name,
            Path.GetDirectoryName(trimmedRoot) ?? rootPath,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue);
    }

    private static void Validate(CatalogRequest request, bool requireDatabase)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);
        ArgumentNullException.ThrowIfNull(request.EntryPaths);
        if (request.EntryPaths.Count == 0)
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
        return $"바로 스캔을 완료했습니다. 후보 {result.CandidateCount:N0}개, 제외 {result.ExcludedDirectoryCount:N0}개, 일치 {result.MatchedCount:N0}개, 파싱 오류 {result.InvalidCount:N0}개, 제외 규칙 경고 {result.DirectoryExclusionIssueCount:N0}개, {errorSummary}를 확인했습니다. 소요 {CatalogElapsedTimeFormatter.Format(report.Progress.Elapsed)}";
    }

    private static string CreateCompletionMessage(
        CatalogResult result,
        IndexScanReport report) =>
        $"스캔과 DB 저장을 완료했습니다. 후보 {result.CandidateCount:N0}개, "
        + $"제외 {result.ExcludedDirectoryCount:N0}개, 일치 {result.MatchedCount:N0}개, 파싱 오류 {result.InvalidCount:N0}개, "
        + $"제외 규칙 경고 {result.DirectoryExclusionIssueCount:N0}개, "
        + $"오류 {report.Errors.Count:N0}개를 확인했습니다. "
        + $"소요 {CatalogElapsedTimeFormatter.Format(report.Progress.Elapsed)}";

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MappingState
    {
        public ScanTraceState Trace { get; } = new();
        public IndexScanProgress CompletedScanProgress { get; set; } = new(0, 0, 0, 0, 0, TimeSpan.Zero, 0);
        public DirectoryDiscoveryProgress CompletedDiscoveryProgress { get; set; } = new(0, 0, 0, 0, 0, TimeSpan.Zero);
        public List<CatalogItem> Items { get; } = [];
        public List<CatalogInvalidItem> InvalidItems { get; } = [];
        public List<CatalogDirectoryExclusionIssue> DirectoryExclusionIssues { get; } = [];
        // Only path identity and traversal semantics are retained when models are streamed.
        public Dictionary<string, bool> MappedPaths { get; } = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public HashSet<string> ExcludedPaths { get; } = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public HashSet<string> ExclusionIssuePaths { get; } = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public int CandidateCount;
        public int NoMatchCount;
        public int ExcludedDirectoryCount;
        public int MatchedCount;
        public int InvalidCount;
        public int FieldIssueRowCount;
        public int DirectoryExclusionIssueCount;
        public bool HasPendingCoverage;
    }

    private sealed class ScanTraceState
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public long Sequence;
        public bool Started;
        public bool IsLastRoot = true;
    }

    private sealed class MappingAccumulator(
        ILoadedProfile profile,
        MappingState state,
        string rootPath,
        string profileRootPath,
        IProfilePathCanonicalizer pathCanonicalizer,
        IProgress<CatalogOperationProgress>? progress,
        bool applyDirectoryExclusions,
        bool retainItems = true,
        ICatalogProjectionWriter? resultWriter = null,
        int attemptId = 1,
        ScanTraceWriter? trace = null,
        int uiUpdateBatchSize = 10)
    {
        private const int MaximumDetailedIssues = 5000;

        public DirectoryTraversalDecision Visit(DiscoveredDirectory directory) =>
            Visit(directory, coveragePending: false);

        public DirectoryTraversalDecision Visit(
            DiscoveredDirectory directory,
            bool coveragePending)
        {
            var relativePath = Path.GetRelativePath(rootPath, directory.FullPath);
            var absolutePath = ResolveProfileInputPath(directory.FullPath, relativePath);
            if (applyDirectoryExclusions && directory.Depth > profile.Descriptor.MaxScanDepth)
                return DirectoryTraversalDecision.ExcludeSubtree;
            // The selected root is an explicit boundary, not one of its own
            // descendants. Every explicitly selected entry keeps this protection,
            // even when another entry has previously excluded it as a descendant.
            if (applyDirectoryExclusions && relativePath != ".")
            {
                var exclusion = profile.EvaluateDirectoryPath(absolutePath);
                if (exclusion.Issues.Count > 0 && state.ExclusionIssuePaths.Add(absolutePath))
                {
                    state.DirectoryExclusionIssueCount++;
                    if (state.DirectoryExclusionIssues.Count < MaximumDetailedIssues)
                        state.DirectoryExclusionIssues.Add(new CatalogDirectoryExclusionIssue(
                            directory.FullPath,
                            relativePath,
                            exclusion.Issues));
                    trace?.ReportDirectoryExclusionIssue(
                        directory.FullPath,
                        relativePath,
                        absolutePath,
                        exclusion.Issues);
                }

                if (exclusion.IsExcluded)
                {
                    if (state.ExcludedPaths.Add(absolutePath)) state.ExcludedDirectoryCount++;
                    trace?.ReportDirectoryExcluded(
                        directory.FullPath,
                        relativePath,
                        absolutePath,
                        exclusion);
                    return DirectoryTraversalDecision.ExcludeSubtree;
                }
            }

            state.HasPendingCoverage |= coveragePending;
            if (state.MappedPaths.TryGetValue(absolutePath, out var shouldPruneDescendants))
            {
                // Do not cache the earlier depth decision: an explicit nested root
                // gets its own traversal budget even when this path was already mapped.
                var duplicateDecision = shouldPruneDescendants
                    || (applyDirectoryExclusions && directory.Depth >= profile.Descriptor.MaxScanDepth)
                    ? DirectoryTraversalDecision.SkipDescendants : DirectoryTraversalDecision.Continue;
                trace?.ReportPreviouslyMappedDirectory(directory.FullPath, absolutePath, relativePath, duplicateDecision);
                return duplicateDecision;
            }
            var mapping = profile.Map(new ProfilePathCandidate(absolutePath));
            state.MappedPaths.Add(absolutePath, mapping.ShouldPruneDescendants);
            state.CandidateCount++;
            switch (mapping.Status)
            {
                case ProfileMapStatus.Success when mapping.Item is not null:
                    var item = new CatalogItem(
                        mapping.Item.FullPath,
                        relativePath,
                        mapping.Item.MatchedRuleId,
                        mapping.Item.Model,
                        mapping.Item.Values,
                        coveragePending)
                    { Resources = mapping.Item.Resources, FieldErrors = mapping.Item.FieldErrors };
                    resultWriter?.Append(item);
                    state.MatchedCount++;
                    if (item.FieldErrors.Count > 0) state.FieldIssueRowCount++;
                    if (retainItems) state.Items.Add(item);
                    progress?.Report(new CatalogOperationProgress(
                        CatalogOperationPhase.Mapping,
                        $"일치 {state.MatchedCount:N0}개 · 경로 {state.CandidateCount:N0}개를 판별했습니다.",
                        state.CandidateCount)
                    {
                        MatchedItem = item,
                        MatchedCount = state.MatchedCount,
                        AttemptId = attemptId,
                    });
                    break;
                case ProfileMapStatus.Invalid:
                    state.InvalidCount++;
                    if (state.InvalidItems.Count < MaximumDetailedIssues)
                        state.InvalidItems.Add(new CatalogInvalidItem(
                            absolutePath,
                            relativePath,
                            mapping.Issues));
                    break;
                case ProfileMapStatus.NoMatch:
                    state.NoMatchCount++;
                    break;
            }

            if (state.CandidateCount % Math.Max(1, uiUpdateBatchSize) == 0)
            {
                progress?.Report(new CatalogOperationProgress(
                    CatalogOperationPhase.Mapping,
                    $"경로 {state.CandidateCount:N0}개를 프로필 규칙으로 판별했습니다.",
                    state.CandidateCount) { AttemptId = attemptId, MatchedCount = state.MatchedCount });
            }

            var traversalDecision = mapping.ShouldPruneDescendants
                || (applyDirectoryExclusions && directory.Depth >= profile.Descriptor.MaxScanDepth)
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

        public CatalogResult BuildResult(bool hasPendingScopes, IndexScanReport? scanReport)
        {
            state.HasPendingCoverage |= hasPendingScopes;
            return new(
                profile.Descriptor,
                state.Items.AsReadOnly(),
                state.InvalidItems.AsReadOnly(),
                state.CandidateCount,
                state.NoMatchCount,
                hasPendingScopes || state.HasPendingCoverage,
                scanReport)
            {
                ExcludedDirectoryCount = state.ExcludedDirectoryCount,
                MatchedCount = state.MatchedCount,
                InvalidCount = state.InvalidCount,
                FieldIssueRowCount = state.FieldIssueRowCount,
                DirectoryExclusionIssueCount = state.DirectoryExclusionIssueCount,
                DirectoryExclusionIssues = state.DirectoryExclusionIssues.AsReadOnly(),
            };
        }
    }

    private sealed class ScanTraceWriter(
        ICatalogScanTraceSink sink,
        CatalogRequest request,
        ProfileDescriptor profile,
        ScanTraceState state)
    {
        private static readonly IReadOnlyDictionary<string, object?> EmptyValues =
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
        private static readonly IReadOnlyList<ProfileMappingIssue> EmptyIssues =
            Array.Empty<ProfileMappingIssue>();

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
                    state.OperationId,
                    Interlocked.Increment(ref state.Sequence),
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

        public void ReportPreviouslyMappedDirectory(
            string discoveredPath,
            string absolutePath,
            string relativePath,
            DirectoryTraversalDecision decision) =>
            ReportCore(new CatalogScanTraceEvent(state.OperationId, Interlocked.Increment(ref state.Sequence),
                DateTimeOffset.UtcNow, CatalogScanTraceKind.DirectoryVisited, profile.Id, profile.DisplayName,
                request.RootPath, discoveredPath, relativePath, absolutePath, null, null, EmptyValues, EmptyIssues,
                decision, "다른 시작 경로에서 이미 판별한 폴더입니다. 결과를 중복 추가하지 않고 현재 시작 경로의 탐색 범위를 적용합니다."));

        public void ReportDirectoryExcluded(
            string discoveredPath,
            string relativePath,
            string absolutePath,
            ProfileDirectoryExclusionResult exclusion)
        {
            try
            {
                var issues = exclusion.Issues.Count == 0
                    ? EmptyIssues
                    : Array.AsReadOnly(exclusion.Issues.ToArray());
                ReportCore(new CatalogScanTraceEvent(
                    state.OperationId,
                    Interlocked.Increment(ref state.Sequence),
                    DateTimeOffset.UtcNow,
                    CatalogScanTraceKind.DirectoryExcluded,
                    profile.Id,
                    profile.DisplayName,
                    request.RootPath,
                    discoveredPath,
                    relativePath,
                    absolutePath,
                    null,
                    exclusion.MatchedRuleId,
                    EmptyValues,
                    issues,
                    DirectoryTraversalDecision.ExcludeSubtree,
                    "전체 절대경로가 폴더 경로 제외 규칙과 일치해 현재 폴더와 하위 탐색을 생략합니다."));
            }
            catch (Exception)
            {
                // Snapshotting diagnostics must not affect traversal.
            }
        }

        public void ReportDirectoryExclusionIssue(
            string discoveredPath,
            string relativePath,
            string absolutePath,
            IReadOnlyList<ProfileMappingIssue> exclusionIssues)
        {
            try
            {
                var issues = exclusionIssues.Count == 0
                    ? EmptyIssues
                    : Array.AsReadOnly(exclusionIssues.ToArray());
                ReportCore(new CatalogScanTraceEvent(
                    state.OperationId,
                    Interlocked.Increment(ref state.Sequence),
                    DateTimeOffset.UtcNow,
                    CatalogScanTraceKind.DirectoryExclusionIssue,
                    profile.Id,
                    profile.DisplayName,
                    request.RootPath,
                    discoveredPath,
                    relativePath,
                    absolutePath,
                    null,
                    null,
                    EmptyValues,
                    issues,
                    DirectoryTraversalDecision.Continue,
                    "폴더 경로 제외 규칙을 평가하는 중 문제가 발생해 안전하게 탐색을 계속합니다."));
            }
            catch (Exception)
            {
                // Snapshotting diagnostics must not affect traversal.
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

        public void ReportIndexError(IndexScanError error)
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
            string? message = null)
        {
            if (kind == CatalogScanTraceKind.Started)
            {
                if (state.Started) return;
                state.Started = true;
            }
            if (kind == CatalogScanTraceKind.Completed && !state.IsLastRoot) return;
            ReportCore(new CatalogScanTraceEvent(
                state.OperationId,
                Interlocked.Increment(ref state.Sequence),
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
        }

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
