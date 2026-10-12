using FindEverything.Application.Indexing;

namespace FindEverything.Application.FileSearch;

/// <summary>
/// Identifies the root and database shared by file search and structured catalog operations.
/// </summary>
public sealed record FileSearchScope(string RootPath, string DatabasePath);

/// <summary>
/// Indicates that a subtree cannot be refreshed until the root has a published index.
/// </summary>
public sealed class FileSearchIndexUnavailableException : InvalidOperationException
{
    public FileSearchIndexUnavailableException(string rootPath)
        : base("A partial refresh requires an existing index for the selected root.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
    }

    public string RootPath { get; }
}

public interface IFileSearchService
{
    Task<IndexScanReport> ScanAsync(
        FileSearchScope scope,
        IProgress<IndexScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-scans one existing directory below the registered root and publishes
    /// only that subtree to the current index. On-demand mode deliberately bypasses
    /// cost-based deferral while retaining the normal exclusion and safety rules.
    /// </summary>
    Task<IndexScanReport> RefreshScopeAsync(
        FileSearchScope scope,
        string refreshPath,
        IProgress<IndexScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<EntrySearchResult> SearchAsync(
        FileSearchScope scope,
        EntrySearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IndexRootStatus> GetStatusAsync(
        FileSearchScope scope,
        CancellationToken cancellationToken = default);
}
