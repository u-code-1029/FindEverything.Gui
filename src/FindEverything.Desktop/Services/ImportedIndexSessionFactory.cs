using FindEverything.Application.Indexing;
using FindEverything.Desktop.Configuration;
using FindEverything.Infrastructure.Persistence;

namespace FindEverything.Desktop.Services;

internal sealed record UnderlyingIndexSessionFactory(IIndexSessionFactory Factory);

/// <summary>Read-only is a service boundary, not merely a disabled button.</summary>
internal sealed class ImportedIndexSessionFactory(UnderlyingIndexSessionFactory underlying, AppPaths paths) : IIndexSessionFactory
{
    public IIndexSession Create(string databasePath)
    {
        var imports = Path.Combine(paths.LocalDataDirectory, "Imports") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(databasePath).StartsWith(imports, StringComparison.OrdinalIgnoreCase)) return underlying.Factory.Create(databasePath);
        var lease = ImportedGenerationLease.Acquire(databasePath);
        try { return new ReadOnlySession(underlying.Factory.Create(databasePath), lease); }
        catch { lease.Dispose(); throw; }
    }

    private sealed class ReadOnlySession(IIndexSession inner, IDisposable generationLease) : IIndexSession
    {
        private IDisposable? _generationLease = generationLease;
        public string DatabasePath => inner.DatabasePath;
        public Task<IndexScanReport> ScanAsync(IndexScanRequest request, IProgress<IndexScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Shared snapshots are read-only. Switch to local results before scanning.");
        public Task<DirectorySearchResult> SearchDirectoriesAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default) => inner.SearchDirectoriesAsync(request, cancellationToken);
        public Task<EntrySearchResult> SearchEntriesAsync(EntrySearchRequest request, CancellationToken cancellationToken = default) => inner.SearchEntriesAsync(request, cancellationToken);
        public Task<IndexRootStatus> GetRootStatusAsync(string rootPath, CancellationToken cancellationToken = default) => inner.GetRootStatusAsync(rootPath, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _generationLease, null)?.Dispose(); }
        }
    }
}
