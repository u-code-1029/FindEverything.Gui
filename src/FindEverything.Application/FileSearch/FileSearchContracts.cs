using FindEverything.Application.Indexing;

namespace FindEverything.Application.FileSearch;

/// <summary>
/// Identifies the root and database shared by file search and structured catalog operations.
/// </summary>
public sealed record FileSearchScope(string RootPath, string DatabasePath);

public interface IFileSearchService
{
    Task<IndexScanReport> ScanAsync(
        FileSearchScope scope,
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
