using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

public sealed record CatalogProjectionScope(string Key, string ProfileId, string RootPath,
    string DatabasePath, string Fingerprint, string CultureName, Guid? IndexScanId);

public sealed record CatalogProjectionRow(string FullPath, string RelativePath,
    string MatchedRuleId, IReadOnlyDictionary<string, object?> Values, bool CoveragePending,
    IReadOnlyDictionary<string, string>? FieldErrors = null)
{
    public IReadOnlyList<string> PreviewFields { get; init; } = [];
    /// <summary>Immutable source path used for imported row selection; null for ordinary local rows.</summary>
    public string? StableKey { get; init; }
    public string? OriginalFullPath { get; init; }
    public IReadOnlyDictionary<string, ProfileFieldResourceInfo> Resources { get; init; } = new Dictionary<string, ProfileFieldResourceInfo>();

    public CatalogItem ToItem() => new(FullPath, RelativePath, MatchedRuleId,
        Values, Values, CoveragePending) { Resources = Resources, FieldErrors = FieldErrors ?? new Dictionary<string, string>() };
}

public sealed record CatalogProjectionSummary(int CandidateCount, int MatchedCount,
    int InvalidCount, int NoMatchCount, int ExcludedCount, bool HasPendingScopes,
    TimeSpan? ScanElapsed, DateTimeOffset UpdatedUtc)
{
    public int FieldIssueRowCount { get; init; }
}

public sealed record CatalogProjectionSnapshot(string Generation,
    CatalogProjectionScope Scope, CatalogProjectionSummary Summary,
    IReadOnlyList<ProfileFieldDescriptor> Fields)
{
    public bool IsBuilding { get; init; }
    public long BuildingRevision { get; init; }
    public long Revision { get; init; }
}

public sealed record CatalogProjectionQuery(string? Keyword = null,
    bool ShowOriginalValues = false, string? SortFieldId = null,
    bool Descending = false)
{
    /// <summary>Limits display-page text-file values. Zero keeps full values; output enumeration is always full.</summary>
    public int TextPreviewCharacters { get; init; }
    public CatalogColumnTarget? SortTarget { get; init; }
    public IReadOnlyList<CatalogColumnFilter> Filters { get; init; } = [];

    public bool HasSamePredicates(CatalogProjectionQuery other) =>
        Keyword == other.Keyword && ShowOriginalValues == other.ShowOriginalValues
        && Filters.SequenceEqual(other.Filters);
}

public interface ICatalogProjectionWriter : IDisposable
{
    string Generation { get; }
    int Count { get; }
    long Revision => 0;
    void Append(CatalogItem item);
    void Flush();
    void Reset();
    CatalogProjectionSnapshot Publish(CatalogProjectionSummary summary,
        CatalogProjectionScope? publishedScope = null);
}

public interface ICatalogProjectionStore
{
    Task<CatalogProjectionSnapshot?> FindAsync(CatalogProjectionScope scope,
        CancellationToken cancellationToken = default);
    ICatalogProjectionWriter Begin(CatalogProjectionScope scope,
        IReadOnlyList<ProfileFieldDescriptor> fields);
    IResultQuerySession<CatalogProjectionRow> OpenQuery(CatalogProjectionSnapshot snapshot,
        CatalogProjectionQuery query);
    IResultQuerySession<CatalogProjectionRow> OpenBuildingQuery(ICatalogProjectionWriter writer,
        CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields,
        CatalogProjectionQuery query);
    Task<CatalogNumericRange?> ReadNumericRangeAsync(CatalogProjectionSnapshot snapshot,
        string fieldId, CancellationToken cancellationToken = default) => Task.FromResult<CatalogNumericRange?>(null);
    /// <summary>Matches at most 500 live-preview paths without materializing any field values.</summary>
    Task<IReadOnlySet<string>?> FindMatchingPathsAsync(CatalogProjectionSnapshot snapshot,
        CatalogProjectionQuery query, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<string>?>(null);
    Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot,
        string fieldId, Func<string, CancellationToken, Task<TextColumnReadResult>> readValue,
        IProgress<TextColumnRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CatalogProjectionSnapshot> UpdateTextCellAsync(CatalogProjectionSnapshot snapshot,
        string fullPath, string fieldId, string value, ProfileFieldResourceInfo resource,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("This data source does not support file edits.");
}

public sealed record TextColumnReadResult(string? Value,
    IReadOnlyList<ProfileMappingIssue> Issues)
{
    public ProfileFieldResourceInfo? Resource { get; init; }
}
public sealed record TextColumnRefreshProgress(int Completed, int Total, int Errors);

public interface IUserInputStore
{
    IReadOnlyDictionary<string, string> GetValues(string profileId, string absolutePath);
    Task SetValueAsync(string profileId, string absolutePath, string fieldId,
        string value, CancellationToken cancellationToken = default);
}
