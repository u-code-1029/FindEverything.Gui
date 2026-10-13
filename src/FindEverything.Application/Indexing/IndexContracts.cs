namespace FindEverything.Application.Indexing;

/// <summary>
/// Creates a short-lived index session for one configured database.
/// </summary>
public interface IIndexSessionFactory
{
    IIndexSession Create(string databasePath);
}

/// <summary>
/// Provides engine-independent indexing, metadata search, and root status operations.
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

    Task<EntrySearchResult> SearchEntriesAsync(
        EntrySearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IndexRootStatus> GetRootStatusAsync(
        string rootPath,
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

    public int TimeoutMilliseconds { get; init; } = 100;
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

    /// <summary>
    /// Optionally inspects each directory before traversal enters it.
    /// <see cref="DirectoryTraversalDecision.SkipDescendants"/> keeps the directory in the
    /// index; <see cref="DirectoryTraversalDecision.ExcludeSubtree"/> omits the directory and
    /// its descendants.
    /// </summary>
    public Func<DiscoveredDirectory, DirectoryTraversalDecision>? InspectDirectory { get; init; }
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

    /// <summary>
    /// Keeps every reported directory in the final result. Callers that consume
    /// <see cref="PageConsumer"/> should set this to <see langword="false"/> so a
    /// large index is not retained twice while it is being processed.
    /// </summary>
    public bool IncludeDirectoriesInResult { get; init; } = true;

    /// <summary>
    /// Consumes each completed database page in index order. Implementations await
    /// the consumer before reading another page or completing the search. This is
    /// required when <see cref="IncludeDirectoriesInResult"/> is <see langword="false"/>.
    /// </summary>
    public Func<IReadOnlyList<IndexedDirectory>, CancellationToken, ValueTask>? PageConsumer { get; init; }

    /// <summary>
    /// Receives a notification for each completed database page. Because
    /// <see cref="Progress{T}"/> may dispatch reports asynchronously, callers that
    /// depend on consumption finishing before this method returns should use
    /// <see cref="PageConsumer"/> instead.
    /// </summary>
    public IProgress<IReadOnlyList<IndexedDirectory>>? PageProgress { get; init; }
}

/// <summary>
/// Indicates that a published index changed while an offset-paged result was being read.
/// The partial result must be discarded before the operation is retried.
/// </summary>
public sealed class IndexSnapshotChangedException : InvalidOperationException
{
    public IndexSnapshotChangedException(
        string rootPath,
        Guid? expectedScanId,
        Guid? actualScanId)
        : base($"The published index for root '{rootPath}' changed while paged results were being read.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
        ExpectedScanId = expectedScanId;
        ActualScanId = actualScanId;
    }

    public string RootPath { get; }

    public Guid? ExpectedScanId { get; }

    public Guid? ActualScanId { get; }
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

public enum IndexedPathKind
{
    File,
    Directory
}

public enum EntrySortField
{
    Name,
    Path,
    Kind,
    Size,
    Created,
    Modified,
    FileType
}

public enum EntrySortDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Describes one server-side page of indexed file-system metadata to retrieve.
/// Date lower bounds are inclusive and upper bounds are exclusive.
/// </summary>
public sealed record EntrySearchRequest(string RootPath)
{
    /// <summary>Optional exact absolute-path restriction, at most 1000 paths per request.</summary>
    public IReadOnlyList<string> FullPaths { get; init; } = [];

    /// <summary>
    /// Case-insensitive, whitespace-delimited literal terms; every term must occur in the name or full path.
    /// </summary>
    public string? Keyword { get; init; }

    public IndexedPathKind? Kind { get; init; }

    public long? MinSizeBytes { get; init; }

    public long? MaxSizeBytes { get; init; }

    public DateTimeOffset? CreatedFromUtc { get; init; }

    public DateTimeOffset? CreatedBeforeUtc { get; init; }

    public DateTimeOffset? ModifiedFromUtc { get; init; }

    public DateTimeOffset? ModifiedBeforeUtc { get; init; }

    public EntrySortField SortBy { get; init; } = EntrySortField.Name;

    public EntrySortDirection SortDirection { get; init; } = EntrySortDirection.Ascending;

    public int Limit { get; init; } = 250;

    public int Offset { get; init; }

    public bool IncludeTotalCount { get; init; } = true;

    public Guid? ExpectedScanId { get; init; }

    public string? AfterCursor { get; init; }
}

public sealed record IndexedPathEntry(
    string FullPath,
    string Name,
    string ParentPath,
    IndexedPathKind Kind,
    long? SizeBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    bool CoveragePending)
{
    public string? OriginalFullPath { get; init; }
    public string? StableKey { get; init; }
}

public sealed record EntrySearchResult(
    IReadOnlyList<IndexedPathEntry> Entries,
    long TotalCount,
    bool HasMore,
    bool HasPendingScopes)
{
    public bool TotalCountIncluded { get; init; } = true;

    public Guid? LastScanId { get; init; }

    public string? NextCursor { get; init; }
}

public enum IndexRootAvailability
{
    DatabaseMissing,
    RootNotIndexed,
    Available
}

/// <summary>
/// Reports whether an index exists for a root and, when available, its last published scan.
/// </summary>
public sealed record IndexRootStatus(
    string RootPath,
    IndexRootAvailability Availability,
    Guid? LastScanId,
    string? LastScopePath,
    IndexScanStatus? LastStatus,
    DateTimeOffset? LastPublishedUtc,
    long EntryCount,
    long LastErrorCount,
    bool HasPendingScopes);
