using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

public enum CatalogOperationPhase
{
    Preparing,
    Scanning,
    Searching,
    Mapping
}

public sealed record CatalogOperationProgress(
    CatalogOperationPhase Phase,
    string Message,
    long ProcessedItems = 0,
    IndexScanProgress? ScanProgress = null)
{
    /// <summary>
    /// Carries a newly matched catalog item while mapping is still in progress.
    /// Consumers may render it immediately instead of waiting for the final result.
    /// </summary>
    public CatalogItem? MatchedItem { get; init; }
}

public sealed record CatalogRequest(
    string ProfileId,
    string RootPath,
    string DatabasePath);

public sealed record CatalogInvalidItem(
    string FullPath,
    string RelativePath,
    IReadOnlyList<ProfileMappingIssue> Issues);

public sealed record CatalogDirectoryExclusionIssue(
    string FullPath,
    string RelativePath,
    IReadOnlyList<ProfileMappingIssue> Issues);

public sealed record CatalogItem(
    string FullPath,
    string RelativePath,
    string MatchedRuleId,
    object Model,
    IReadOnlyDictionary<string, object?> Values,
    bool CoveragePending);

public sealed record CatalogResult(
    ProfileDescriptor Profile,
    IReadOnlyList<CatalogItem> Items,
    IReadOnlyList<CatalogInvalidItem> InvalidItems,
    int CandidateCount,
    int NoMatchCount,
    bool HasPendingScopes,
    IndexScanReport? ScanReport)
{
    public DirectoryDiscoveryReport? DiscoveryReport { get; init; }

    public int ExcludedDirectoryCount { get; init; }

    public IReadOnlyList<CatalogDirectoryExclusionIssue> DirectoryExclusionIssues { get; init; } =
        Array.Empty<CatalogDirectoryExclusionIssue>();
}

/// <summary>
/// Indicates that an existing catalog index cannot be loaded because either the
/// database is missing or the requested root has never been published to it.
/// </summary>
public sealed class CatalogIndexUnavailableException : InvalidOperationException
{
    public CatalogIndexUnavailableException(
        IndexRootAvailability availability,
        string databasePath,
        string rootPath)
        : base(CreateMessage(availability, databasePath, rootPath))
    {
        if (availability == IndexRootAvailability.Available)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availability),
                availability,
                "An available index cannot produce an unavailable-index exception.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        Availability = availability;
        DatabasePath = databasePath;
        RootPath = rootPath;
    }

    public IndexRootAvailability Availability { get; }

    public string DatabasePath { get; }

    public string RootPath { get; }

    private static string CreateMessage(
        IndexRootAvailability availability,
        string databasePath,
        string rootPath) => availability switch
    {
        IndexRootAvailability.DatabaseMissing =>
            $"The index database '{databasePath}' does not exist. Create the index before loading catalog data.",
        IndexRootAvailability.RootNotIndexed =>
            $"The exact root '{rootPath}' has not been indexed in database '{databasePath}'. Index this root before loading catalog data.",
        IndexRootAvailability.Available =>
            "The catalog index is available.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(availability),
            availability,
            "Unknown index-root availability."),
    };
}

public interface ICatalogService
{
    Task<CatalogResult> LoadExistingAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CatalogResult> DiscoverAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CatalogResult> ScanAndLoadAsync(
        CatalogRequest request,
        IProgress<CatalogOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPathLauncher
{
    void OpenDirectory(string path);

    void OpenPath(string path);

    void ShowInFolder(string path);
}
