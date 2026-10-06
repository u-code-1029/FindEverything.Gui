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

        return ExecuteSerializedAsync(async () =>
        {
            var directories = new List<IndexedDirectory>();
            var hasPendingScopes = false;
            var offset = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _store.SearchAsync(new Engine.SearchQuery
                {
                    RootPath = request.RootPath,
                    Kind = Engine.EntryKind.Directory,
                    Limit = request.PageSize,
                    Offset = offset
                }, cancellationToken).ConfigureAwait(false);

                hasPendingScopes |= page.HasPendingScopes;
                foreach (var entry in page.Entries)
                {
                    directories.Add(new IndexedDirectory(
                        entry.FullPath,
                        entry.Name,
                        entry.ParentPath,
                        entry.CreatedUtc,
                        entry.ModifiedUtc,
                        entry.CoveragePending));
                }

                if (!page.HasMore)
                    break;
                if (page.Entries.Count == 0)
                    throw new InvalidDataException("The index returned an empty page with more results available.");

                offset = checked(offset + page.Entries.Count);
            }

            return new DirectorySearchResult(directories.AsReadOnly(), hasPendingScopes);
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
            using var processLease = await ProcessIndexOperationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
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

        return new Engine.ScanRequest(request.RootPath)
        {
            ScopePath = request.ScopePath,
            OnDemand = request.OnDemand,
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
            IgnoreCase = rule.IgnoreCase
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
