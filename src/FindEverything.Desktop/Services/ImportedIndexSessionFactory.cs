using FindEverything.Application.Indexing;
using FindEverything.Desktop.Configuration;

namespace FindEverything.Desktop.Services;

internal sealed record UnderlyingIndexSessionFactory(IIndexSessionFactory Factory);

/// <summary>Read-only is a service boundary, not merely a disabled button.</summary>
internal sealed class ImportedIndexSessionFactory(UnderlyingIndexSessionFactory underlying, AppPaths paths) : IIndexSessionFactory
{
    public IIndexSession Create(string databasePath)
    {
        var session = underlying.Factory.Create(databasePath);
        var imports = Path.Combine(paths.LocalDataDirectory, "Imports") + Path.DirectorySeparatorChar;
        return Path.GetFullPath(databasePath).StartsWith(imports, StringComparison.OrdinalIgnoreCase)
            ? new ReadOnlySession(session) : session;
    }

    private sealed class ReadOnlySession(IIndexSession inner) : IIndexSession
    {
        public string DatabasePath => inner.DatabasePath;
        public Task<IndexScanReport> ScanAsync(IndexScanRequest request, IProgress<IndexScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Shared snapshots are read-only. Switch to local results before scanning.");
        public Task<DirectorySearchResult> SearchDirectoriesAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default) => inner.SearchDirectoriesAsync(request, cancellationToken);
        public Task<EntrySearchResult> SearchEntriesAsync(EntrySearchRequest request, CancellationToken cancellationToken = default) => inner.SearchEntriesAsync(request, cancellationToken);
        public Task<IndexRootStatus> GetRootStatusAsync(string rootPath, CancellationToken cancellationToken = default) => inner.GetRootStatusAsync(rootPath, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
