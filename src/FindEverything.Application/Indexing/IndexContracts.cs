namespace FindEverything.Application.Indexing;

/// <summary>
/// Creates a short-lived index session for one configured database.
/// </summary>
public interface IIndexSessionFactory
{
    IIndexSession Create(string databasePath);
}

/// <summary>
/// Provides engine-independent indexing and directory search operations.
/// </summary>
public interface IIndexSession : IAsyncDisposable
{
    string DatabasePath { get; }

    Task<IndexScanReport> ScanAsync(
        IndexScanRequest request,
        IProgress<IndexScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DirectorySearchResult> SearchDirectoriesAsync(
        DirectorySearchRequest request,
        CancellationToken cancellationToken = default);
}

public enum IndexRegexMatchMode
{
    Full,
    Partial
}

public sealed record IndexDirectoryNameRegex(string Pattern)
{
    public IndexRegexMatchMode MatchMode { get; init; } = IndexRegexMatchMode.Full;

    public bool? IgnoreCase { get; init; }
}

public sealed record IndexDeferralPolicy
{
    public IReadOnlyList<string> DirectoryNames { get; init; } = [];

    public IReadOnlyList<string> Paths { get; init; } = [];

    public long? HistoricalEntryThreshold { get; init; }

    public TimeSpan? HistoricalDurationThreshold { get; init; }

    public long? EntryBudget { get; init; }

    public TimeSpan? TimeBudget { get; init; }

    public int MaxPendingScopes { get; init; } = 256;
}

public sealed record IndexScanOptions
{
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } = [];

    public IReadOnlyList<IndexDirectoryNameRegex> ExcludedDirectoryNameRegexes { get; init; } = [];

    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    public IReadOnlyList<string> ExcludedFilePatterns { get; init; } = [];

    public int BatchSize { get; init; } = 256;

    public int MaxEntriesPerSecond { get; init; } = 2000;

    public TimeSpan DirectoryDelay { get; init; } = TimeSpan.FromMilliseconds(5);

    public int MaxDepth { get; init; } = 256;

    public int MaxRecordedErrors { get; init; } = 100;

    public int EnumerationBufferSize { get; init; } = 16 * 1024;

    public int MaxRecordedExclusions { get; init; } = 1000;

    public int MaxTrackedDirectoryNames { get; init; } = 4096;

    public IndexDeferralPolicy Deferral { get; init; } = new();
}

public sealed record IndexScanRequest(string RootPath)
{
    public string? ScopePath { get; init; }

    public IndexScanOptions Options { get; init; } = new();

    public bool OnDemand { get; init; }
}

public enum IndexScanStatus
{
    Completed,
    Partial,
    Cancelled,
    Deferred
}

public enum IndexDeferralReason
{
    ExplicitRule,
    HistoricalEntryCount,
    HistoricalDuration,
    EntryBudget,
    TimeBudget,
    PendingLimit
}

public sealed record IndexScanProgress(
    long Entries,
    long Directories,
    long ExcludedEntries,
    long SkippedLinks,
    long ErrorCount,
    TimeSpan Elapsed,
    long PendingDirectories);

public sealed record IndexScanError(string Path, string Message);

public sealed record IndexPendingScope(
    string RootPath,
    string ScopePath,
    IndexDeferralReason Reason,
    long? EstimatedEntries,
    TimeSpan? EstimatedDuration,
    DateTimeOffset DeferredUtc);

public sealed record IndexScanReport(
    Guid ScanId,
    string RootPath,
    string ScopePath,
    IndexScanStatus Status,
    IndexScanProgress Progress,
    IReadOnlyList<IndexScanError> Errors,
    IReadOnlyList<IndexPendingScope> PendingScopes);

public sealed record DirectorySearchRequest(string RootPath)
{
    public int PageSize { get; init; } = 1000;
}

public sealed record IndexedDirectory(
    string FullPath,
    string Name,
    string ParentPath,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    bool CoveragePending);

public sealed record DirectorySearchResult(
    IReadOnlyList<IndexedDirectory> Directories,
    bool HasPendingScopes);
