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

    public CatalogItem ToItem() => new(FullPath, RelativePath, MatchedRuleId,
        Values, Values, CoveragePending);
}

public sealed record CatalogProjectionSummary(int CandidateCount, int MatchedCount,
    int InvalidCount, int NoMatchCount, int ExcludedCount, bool HasPendingScopes,
    TimeSpan? ScanElapsed, DateTimeOffset UpdatedUtc);

public sealed record CatalogProjectionSnapshot(string Generation,
    CatalogProjectionScope Scope, CatalogProjectionSummary Summary,
    IReadOnlyList<ProfileFieldDescriptor> Fields);

public sealed record CatalogProjectionQuery(string? Keyword = null,
    bool ShowOriginalValues = false, string? SortFieldId = null,
    bool Descending = false)
{
    /// <summary>Limits display-page text-file values. Zero keeps full values; output enumeration is always full.</summary>
    public int TextPreviewCharacters { get; init; }
}

public interface ICatalogProjectionWriter : IDisposable
{
    string Generation { get; }
    int Count { get; }
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
    Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot,
        string fieldId, Func<string, CancellationToken, Task<TextColumnReadResult>> readValue,
        IProgress<TextColumnRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record TextColumnReadResult(string? Value,
    IReadOnlyList<ProfileMappingIssue> Issues);
public sealed record TextColumnRefreshProgress(int Completed, int Total, int Errors);

public interface IUserInputStore
{
    IReadOnlyDictionary<string, string> GetValues(string profileId, string absolutePath);
    Task SetValueAsync(string profileId, string absolutePath, string fieldId,
        string value, CancellationToken cancellationToken = default);
}
