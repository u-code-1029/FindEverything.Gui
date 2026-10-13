using FindEverything.Application.Indexing;
using Engine = global::FindEverything.Engine;

namespace FindEverything.Infrastructure.FindEverything;

internal sealed class FindEverythingIndexSession : IIndexSession
{
    private readonly Engine.SqliteIndexStore _store;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private bool _disposed;

    public FindEverythingIndexSession(string databasePath)
    {
        _store = new Engine.SqliteIndexStore(databasePath);
    }

    public string DatabasePath => _store.DatabasePath;

    public Task<IndexScanReport> ScanAsync(
        IndexScanRequest request,
        IProgress<IndexScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSerializedAsync(async () =>
        {
            var engine = new Engine.IndexingEngine(new Engine.FileSystemMetadataScanner(), _store);
            var engineProgress = progress is null ? null : new ScanProgressAdapter(progress);
            var report = await engine.ScanAsync(
                ToEngineRequest(request),
                engineProgress,
                cancellationToken).ConfigureAwait(false);

            return ToApplicationReport(report);
        }, cancellationToken);
    }

    public Task<DirectorySearchResult> SearchDirectoriesAsync(
        DirectorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        if (request.PageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.PageSize,
                "Page size must be between 1 and 1000.");
        }
        if (!request.IncludeDirectoriesInResult && request.PageConsumer is null)
        {
            throw new ArgumentException(
                "A page consumer is required when directories are not included in the result.",
                nameof(request));
        }

        return ExecuteSerializedAsync(async () =>
        {
            var directories = request.IncludeDirectoriesInResult
                ? new List<IndexedDirectory>()
                : null;
            var hasPendingScopes = false;
            string? cursor = null;
            var snapshot = await _store.GetRootStatusAsync(
                request.RootPath,
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _store.SearchAsync(new Engine.SearchQuery
                {
                    RootPath = request.RootPath,
                    Kind = Engine.EntryKind.Directory,
                    Limit = request.PageSize,
                    IncludeTotalCount = false,
                    ExpectedScanId = snapshot?.LastScanId,
                    AfterCursor = cursor
                }, cancellationToken).ConfigureAwait(false);

                hasPendingScopes |= page.HasPendingScopes;
                var mappedPage = page.Entries.Select(entry =>
                    new IndexedDirectory(
                        entry.FullPath,
                        entry.Name,
                        entry.ParentPath,
                        entry.CreatedUtc,
                        entry.ModifiedUtc,
                        entry.CoveragePending))
                    .ToArray();
                directories?.AddRange(mappedPage);
                hasPendingScopes |= mappedPage.Any(static directory => directory.CoveragePending);
                if (request.PageConsumer is not null)
                {
                    await request.PageConsumer(mappedPage, cancellationToken).ConfigureAwait(false);
                }
                request.PageProgress?.Report(mappedPage);

                if (!page.HasMore)
                    break;
                if (page.Entries.Count == 0)
                    throw new InvalidDataException("The index returned an empty page with more results available.");

                cursor = page.NextCursor
                    ?? throw new InvalidDataException("The index returned more results without a sequential cursor.");
            }

            // Each page checks its generation in the row read transaction. This
            // final fence also notices publication during the last consumer call.
            await EnsureSnapshotUnchangedAsync(
                request.RootPath,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            return new DirectorySearchResult(
                directories is null
                    ? Array.Empty<IndexedDirectory>()
                    : directories.AsReadOnly(),
                hasPendingScopes);
        }, cancellationToken);
    }

    private async Task EnsureSnapshotUnchangedAsync(
        string rootPath,
        Engine.IndexRootStatus? expected,
        CancellationToken cancellationToken)
    {
        var actual = await _store.GetRootStatusAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (Equals(expected, actual))
            return;

        throw new IndexSnapshotChangedException(
            rootPath,
            expected?.LastScanId,
            actual?.LastScanId);
    }

    public Task<EntrySearchResult> SearchEntriesAsync(
        EntrySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEntrySearchRequest(request);

        return ExecuteSerializedAsync(async () =>
        {
            var result = await _store.SearchAsync(new Engine.SearchQuery
            {
                RootPath = request.RootPath,
                FullPaths = request.FullPaths,
                SearchText = request.Keyword,
                Kind = request.Kind is null ? null : ToEngineKind(request.Kind.Value),
                MinSizeBytes = request.MinSizeBytes,
                MaxSizeBytes = request.MaxSizeBytes,
                CreatedFromUtc = request.CreatedFromUtc,
                CreatedBeforeUtc = request.CreatedBeforeUtc,
                ModifiedFromUtc = request.ModifiedFromUtc,
                ModifiedBeforeUtc = request.ModifiedBeforeUtc,
                SortBy = ToEngineSortField(request.SortBy),
                SortDirection = ToEngineSortDirection(request.SortDirection),
                Limit = request.Limit,
                Offset = request.Offset,
                IncludeTotalCount = request.IncludeTotalCount,
                ExpectedScanId = request.ExpectedScanId,
                AfterCursor = request.AfterCursor
            }, cancellationToken).ConfigureAwait(false);

            return new EntrySearchResult(
                result.Entries.Select(ToApplicationEntry).ToArray(),
                result.TotalCount,
                result.HasMore,
                result.HasPendingScopes)
            {
                TotalCountIncluded = result.TotalCountIncluded,
                LastScanId = result.LastScanId,
                NextCursor = result.NextCursor,
            };
        }, cancellationToken);
    }

    public Task<IndexRootStatus> GetRootStatusAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        return ExecuteSerializedAsync(async () =>
        {
            if (!File.Exists(DatabasePath))
                return MissingRootStatus(normalizedRoot);

            try
            {
                var status = await _store.GetRootStatusAsync(
                    normalizedRoot,
                    cancellationToken).ConfigureAwait(false);
                if (status is null || status.LastStatus is null || status.LastPublishedUtc is null)
                    return UnindexedRootStatus(normalizedRoot);

                return new IndexRootStatus(
                    status.RootPath,
                    IndexRootAvailability.Available,
                    status.LastScanId,
                    status.LastScopePath,
                    status.LastStatus is null ? null : ToApplicationStatus(status.LastStatus.Value),
                    status.LastPublishedUtc,
                    status.EntryCount,
                    status.LastErrorCount,
                    status.HasPendingScopes);
            }
            catch (FileNotFoundException) when (!File.Exists(DatabasePath))
            {
                return MissingRootStatus(normalizedRoot);
            }
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation().ConfigureAwait(false);
        }
        catch (Engine.PublishedIndexChangedException error)
        {
            throw new IndexSnapshotChangedException(error.RootPath, error.ExpectedScanId, error.ActualScanId);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static Engine.ScanRequest ToEngineRequest(IndexScanRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        ArgumentNullException.ThrowIfNull(request.Options);
        var options = request.Options;
        ArgumentNullException.ThrowIfNull(options.ExcludedDirectoryNames);
        ArgumentNullException.ThrowIfNull(options.ExcludedDirectoryNameRegexes);
        ArgumentNullException.ThrowIfNull(options.ExcludedPaths);
        ArgumentNullException.ThrowIfNull(options.ExcludedFilePatterns);
        ArgumentNullException.ThrowIfNull(options.Deferral);

        var deferral = options.Deferral;
        ArgumentNullException.ThrowIfNull(deferral.DirectoryNames);
        ArgumentNullException.ThrowIfNull(deferral.Paths);
        var inspectDirectory = request.InspectDirectory;

        return new Engine.ScanRequest(request.RootPath)
        {
            ScopePath = request.ScopePath,
            OnDemand = request.OnDemand,
            InspectDirectory = inspectDirectory is null
                ? null
                : candidate => ToEngineTraversalDecision(
                    inspectDirectory(ToApplicationCandidate(candidate, request.RootPath))),
            Options = new Engine.ScanOptions
            {
                ExcludedDirectoryNames = options.ExcludedDirectoryNames.ToArray(),
                ExcludedDirectoryNameRegexes = options.ExcludedDirectoryNameRegexes
                    .Select(ToEngineRegex)
                    .ToArray(),
                ExcludedPaths = options.ExcludedPaths.ToArray(),
                ExcludedFilePatterns = options.ExcludedFilePatterns.ToArray(),
                BatchSize = options.BatchSize,
                MaxEntriesPerSecond = options.MaxEntriesPerSecond,
                DirectoryDelay = options.DirectoryDelay,
                MaxDepth = options.MaxDepth,
                MaxRecordedErrors = options.MaxRecordedErrors,
                EnumerationBufferSize = options.EnumerationBufferSize,
                MaxRecordedExclusions = options.MaxRecordedExclusions,
                MaxTrackedDirectoryNames = options.MaxTrackedDirectoryNames,
                Deferral = new Engine.DeferralPolicy
                {
                    DirectoryNames = deferral.DirectoryNames.ToArray(),
                    Paths = deferral.Paths.ToArray(),
                    HistoricalEntryThreshold = deferral.HistoricalEntryThreshold,
                    HistoricalDurationThreshold = deferral.HistoricalDurationThreshold,
                    EntryBudget = deferral.EntryBudget,
                    TimeBudget = deferral.TimeBudget,
                    MaxPendingScopes = deferral.MaxPendingScopes
                }
            }
        };
    }

    private static DiscoveredDirectory ToApplicationCandidate(Engine.DirectoryCandidate candidate, string rootPath) =>
        new(
            candidate.FullPath,
            candidate.Name,
            candidate.ParentPath
                ?? Path.GetDirectoryName(candidate.FullPath)
                ?? candidate.FullPath,
            candidate.CreatedUtc,
            candidate.ModifiedUtc)
        {
            CoveragePending = candidate.CoveragePending,
            // Engine depth is relative to ScopePath during a partial refresh. Profile
            // limits remain relative to the selected index root instead.
            Depth = RelativeDepth(rootPath, candidate.FullPath),
        };

    private static int RelativeDepth(string rootPath, string path)
    {
        var relative = Path.GetRelativePath(rootPath, path);
        return relative == "." ? 0 : relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static Engine.DirectoryTraversalDecision ToEngineTraversalDecision(
        DirectoryTraversalDecision decision) => decision switch
        {
            DirectoryTraversalDecision.Continue => Engine.DirectoryTraversalDecision.Continue,
            DirectoryTraversalDecision.SkipDescendants =>
                Engine.DirectoryTraversalDecision.SkipDescendants,
            DirectoryTraversalDecision.ExcludeSubtree =>
                Engine.DirectoryTraversalDecision.ExcludeSubtree,
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "Unknown directory traversal decision."),
        };

    private static Engine.DirectoryNameRegex ToEngineRegex(IndexDirectoryNameRegex rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new Engine.DirectoryNameRegex
        {
            Pattern = rule.Pattern,
            MatchMode = rule.MatchMode switch
            {
                IndexRegexMatchMode.Full => Engine.RegexMatchMode.Full,
                IndexRegexMatchMode.Partial => Engine.RegexMatchMode.Partial,
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.MatchMode, "Unknown regex match mode.")
            },
            IgnoreCase = rule.IgnoreCase,
            TimeoutMilliseconds = rule.TimeoutMilliseconds,
        };
    }

    private static IndexScanReport ToApplicationReport(Engine.ScanReport report) => new(
        report.ScanId,
        report.RootPath,
        report.ScopePath,
        ToApplicationStatus(report.Status),
        ToApplicationProgress(report.Progress),
        report.Errors.Select(error => new IndexScanError(error.Path, error.Message)).ToArray(),
        report.PendingScopes.Select(scope => new IndexPendingScope(
            scope.RootPath,
            scope.ScopePath,
            ToApplicationDeferralReason(scope.Reason),
            scope.EstimatedEntries,
            scope.EstimatedDuration,
            scope.DeferredUtc)).ToArray());

    private static IndexScanStatus ToApplicationStatus(Engine.ScanStatus status) => status switch
    {
        Engine.ScanStatus.Completed => IndexScanStatus.Completed,
        Engine.ScanStatus.Partial => IndexScanStatus.Partial,
        Engine.ScanStatus.Cancelled => IndexScanStatus.Cancelled,
        Engine.ScanStatus.Deferred => IndexScanStatus.Deferred,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown scan status.")
    };

    private static IndexedPathEntry ToApplicationEntry(Engine.IndexedEntry entry) => new(
        entry.FullPath,
        entry.Name,
        entry.ParentPath,
        entry.Kind switch
        {
            Engine.EntryKind.File => IndexedPathKind.File,
            Engine.EntryKind.Directory => IndexedPathKind.Directory,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown entry kind.")
        },
        entry.SizeBytes,
        entry.CreatedUtc,
        entry.ModifiedUtc,
        entry.CoveragePending);

    private static Engine.EntryKind ToEngineKind(IndexedPathKind kind) => kind switch
    {
        IndexedPathKind.File => Engine.EntryKind.File,
        IndexedPathKind.Directory => Engine.EntryKind.Directory,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown entry kind.")
    };

    private static Engine.EntrySortField ToEngineSortField(EntrySortField sortField) => sortField switch
    {
        EntrySortField.Name => Engine.EntrySortField.Name,
        EntrySortField.Path => Engine.EntrySortField.Path,
        EntrySortField.Kind => Engine.EntrySortField.Kind,
        EntrySortField.Size => Engine.EntrySortField.Size,
        EntrySortField.Created => Engine.EntrySortField.Created,
        EntrySortField.Modified => Engine.EntrySortField.Modified,
        EntrySortField.FileType => Engine.EntrySortField.FileType,
        _ => throw new ArgumentOutOfRangeException(nameof(sortField), sortField, "Unknown sort field.")
    };

    private static Engine.SortDirection ToEngineSortDirection(EntrySortDirection sortDirection) =>
        sortDirection switch
        {
            EntrySortDirection.Ascending => Engine.SortDirection.Ascending,
            EntrySortDirection.Descending => Engine.SortDirection.Descending,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sortDirection),
                sortDirection,
                "Unknown sort direction.")
        };

    private static void ValidateEntrySearchRequest(EntrySearchRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        ArgumentNullException.ThrowIfNull(request.FullPaths);
        if (request.FullPaths.Count > 1000 || request.FullPaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Exact path searches require at most 1000 absolute paths.", nameof(request));
        if (request.Limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request), "Limit must be between 1 and 1000.");
        if (request.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Offset must be nonnegative.");
        if (!Enum.IsDefined(request.SortBy) || !Enum.IsDefined(request.SortDirection)
            || request.Kind is { } kind && !Enum.IsDefined(kind))
        {
            throw new ArgumentException("The search request contains an unsupported enum value.", nameof(request));
        }

        if (request.MinSizeBytes is < 0 || request.MaxSizeBytes is < 0
            || request.MinSizeBytes > request.MaxSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Size bounds must be nonnegative and the minimum cannot exceed the maximum.");
        }

        ValidateDateRange(request.CreatedFromUtc, request.CreatedBeforeUtc, nameof(request));
        ValidateDateRange(request.ModifiedFromUtc, request.ModifiedBeforeUtc, nameof(request));
    }

    private static void ValidateDateRange(
        DateTimeOffset? from,
        DateTimeOffset? before,
        string parameterName)
    {
        if (from is not null && before is not null && from >= before)
            throw new ArgumentException("Date lower bounds must precede upper bounds.", parameterName);
    }

    private static IndexRootStatus MissingRootStatus(string rootPath) => new(
        rootPath,
        IndexRootAvailability.DatabaseMissing,
        null,
        null,
        null,
        null,
        0,
        0,
        false);

    private static IndexRootStatus UnindexedRootStatus(string rootPath) => new(
        rootPath,
        IndexRootAvailability.RootNotIndexed,
        null,
        null,
        null,
        null,
        0,
        0,
        false);

    private static IndexDeferralReason ToApplicationDeferralReason(Engine.DeferralReason reason) => reason switch
    {
        Engine.DeferralReason.ExplicitRule => IndexDeferralReason.ExplicitRule,
        Engine.DeferralReason.HistoricalEntryCount => IndexDeferralReason.HistoricalEntryCount,
        Engine.DeferralReason.HistoricalDuration => IndexDeferralReason.HistoricalDuration,
        Engine.DeferralReason.EntryBudget => IndexDeferralReason.EntryBudget,
        Engine.DeferralReason.TimeBudget => IndexDeferralReason.TimeBudget,
        Engine.DeferralReason.PendingLimit => IndexDeferralReason.PendingLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown deferral reason.")
    };

    private static IndexScanProgress ToApplicationProgress(Engine.ScanProgress progress) => new(
        progress.Entries,
        progress.Directories,
        progress.ExcludedEntries,
        progress.SkippedLinks,
        progress.ErrorCount,
        progress.Elapsed,
        progress.PendingDirectories);

    private sealed class ScanProgressAdapter(IProgress<IndexScanProgress> target) : IProgress<Engine.ScanProgress>
    {
        public void Report(Engine.ScanProgress value) => target.Report(ToApplicationProgress(value));
    }
}
