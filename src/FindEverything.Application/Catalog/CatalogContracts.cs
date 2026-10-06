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
    IndexScanProgress? ScanProgress = null);

public sealed record CatalogRequest(
    string ProfileId,
    string RootPath,
    string DatabasePath);

public sealed record CatalogInvalidItem(
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
    IndexScanReport? ScanReport);

public interface ICatalogService
{
    Task<CatalogResult> LoadExistingAsync(
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
