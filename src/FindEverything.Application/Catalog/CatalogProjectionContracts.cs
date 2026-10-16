using FindEverything.Application.Results;
using FindEverything.Profile.Runtime;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FindEverything.Application.Catalog;

public sealed record CatalogProjectionScope(string Key, string ProfileId, string RootPath,
    string DatabasePath, string Fingerprint, string CultureName, Guid? IndexScanId)
{
    public const int MaximumRootPaths = 64;
    public const int MaximumRootPathsIdentityLength = 256 * 1024;
    private const int MaximumRootPathLength = 32768;

    /// <summary>Canonical ordered JSON; null retains the existing single-root identity and equality.</summary>
    public string? RootPathsIdentity { get; init; }

    // Arrays are computed, not record fields: a deserialized scope must have
    // value equality rather than depending on an array's object identity.
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveRootPaths => RootPathsIdentity is null
        ? [RootPath] : ReadRootPathsIdentity(RootPathsIdentity);

    [JsonIgnore]
    public bool IsMultiRoot => RootPathsIdentity is not null && EffectiveRootPaths.Count > 1;

    public static string? CreateRootPathsIdentity(IEnumerable<string> rootPaths)
    {
        var paths = NormalizeRootPaths(rootPaths);
        if (paths.Length == 1) return null;
        var identity = JsonSerializer.Serialize(paths);
        if (identity.Length > MaximumRootPathsIdentityLength)
            throw new ArgumentException("The entry-path identity exceeds the supported size limit.", nameof(rootPaths));
        return identity;
    }

    /// <summary>Preserves a single root's GUID; every root/version contributes to a multi-root token.</summary>
    public static Guid? CreateIndexVersion(IEnumerable<KeyValuePair<string, Guid?>> rootVersions)
    {
        ArgumentNullException.ThrowIfNull(rootVersions);
        var versions = rootVersions.Take(MaximumRootPaths + 1).ToArray();
        if (versions.Length is 0 or > MaximumRootPaths)
            throw new ArgumentException($"Specify between 1 and {MaximumRootPaths} entry paths.", nameof(rootVersions));
        var normalized = versions.Select(pair => new
        {
            Root = NormalizeRootPath(pair.Key).ToUpperInvariant(), ScanId = pair.Value,
        }).GroupBy(pair => pair.Root, StringComparer.Ordinal).Select(group =>
        {
            if (group.Select(pair => pair.ScanId).Distinct().Count() != 1)
                throw new ArgumentException("A root cannot have conflicting index versions.", nameof(rootVersions));
            return group.First();
        }).OrderBy(pair => pair.Root, StringComparer.Ordinal).ToArray();
        if (normalized.Length == 1) return normalized[0].ScanId;
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized));
        return new Guid(hash.AsSpan(0, 16));
    }

    public void ValidateRootPaths()
    {
        var root = NormalizeRootPath(RootPath);
        if (RootPathsIdentity is null) return;
        var paths = ReadRootPathsIdentity(RootPathsIdentity);
        if (!string.Equals(root, paths[0], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The primary root must be the first canonical entry path.", nameof(RootPath));
    }

    private static string[] NormalizeRootPaths(IEnumerable<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        var paths = rootPaths.Take(MaximumRootPaths + 1).ToArray();
        if (paths.Length is 0 or > MaximumRootPaths)
            throw new ArgumentException($"Specify between 1 and {MaximumRootPaths} entry paths.", nameof(rootPaths));
        return paths.Select(NormalizeRootPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeRootPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumRootPathLength || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An entry path must be a supported fully qualified path.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string[] ReadRootPathsIdentity(string identity)
    {
        if (identity.Length > MaximumRootPathsIdentityLength)
            throw new InvalidDataException("The entry-path identity exceeds the supported size limit.");
        try
        {
            var serialized = JsonSerializer.Deserialize<string[]>(identity)
                ?? throw new InvalidDataException("The entry-path identity must contain an array of paths.");
            var paths = NormalizeRootPaths(serialized);
            if (paths.Length < 2 || !string.Equals(identity, JsonSerializer.Serialize(paths), StringComparison.Ordinal))
                throw new InvalidDataException("A multi-root identity must contain canonical, distinct, ordered paths.");
            return paths;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The entry-path identity is invalid.", exception);
        }
    }
}

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
    /// <summary>Reads a completed matching publication independently of newer/incomplete engine scans.</summary>
    Task<CatalogProjectionSnapshot?> FindLastPublishedAsync(CatalogProjectionScope scope,
        CancellationToken cancellationToken = default) => FindAsync(scope, cancellationToken);
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
