using FindEverything.Application.Indexing;
using FindEverything.Application.Options;

namespace FindEverything.Application.FileSearch;

internal sealed class FileSearchService(
    IIndexSessionFactory sessionFactory,
    IValidatedSettingsState<IndexingOptions> indexingSettings) : IFileSearchService
{
    public async Task<IndexScanReport> ScanAsync(
        FileSearchScope scope,
        IProgress<IndexScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var operationSettings = indexingSettings.Current;
        await using var session = sessionFactory.Create(scope.DatabasePath);
        var report = await session.ScanAsync(
            new IndexScanRequest(scope.RootPath)
            {
                Options = new IndexScanOptions
                {
                    MaxEntriesPerSecond = operationSettings.MaxEntriesPerSecond,
                    DirectoryDelay = TimeSpan.FromMilliseconds(
                        operationSettings.DirectoryDelayMilliseconds),
                },
            },
            progress,
            cancellationToken).ConfigureAwait(false);
        if (report.Status == IndexScanStatus.Cancelled)
            throw new OperationCanceledException(cancellationToken);
        return report;
    }

    public async Task<IndexScanReport> RefreshScopeAsync(
        FileSearchScope scope,
        string refreshPath,
        IProgress<IndexScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var normalizedRoot = NormalizeDirectory(scope.RootPath);
        var normalizedRefreshPath = ValidateRefreshPath(normalizedRoot, refreshPath);
        var operationSettings = indexingSettings.Current;
        await using var session = sessionFactory.Create(scope.DatabasePath);
        var rootStatus = await session.GetRootStatusAsync(
            normalizedRoot,
            cancellationToken).ConfigureAwait(false);
        if (rootStatus.Availability != IndexRootAvailability.Available)
        {
            throw new FileSearchIndexUnavailableException(normalizedRoot);
        }

        var report = await session.ScanAsync(
            new IndexScanRequest(normalizedRoot)
            {
                ScopePath = normalizedRefreshPath,
                OnDemand = true,
                Options = new IndexScanOptions
                {
                    MaxEntriesPerSecond = operationSettings.MaxEntriesPerSecond,
                    DirectoryDelay = TimeSpan.FromMilliseconds(
                        operationSettings.DirectoryDelayMilliseconds),
                },
            },
            progress,
            cancellationToken).ConfigureAwait(false);
        if (report.Status == IndexScanStatus.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return report;
    }

    public async Task<EntrySearchResult> SearchAsync(
        FileSearchScope scope,
        EntrySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ValidateSearchRequest(scope, request);
        await using var session = sessionFactory.Create(scope.DatabasePath);
        return await session.SearchEntriesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IndexRootStatus> GetStatusAsync(
        FileSearchScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        await using var session = sessionFactory.Create(scope.DatabasePath);
        return await session.GetRootStatusAsync(scope.RootPath, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateScope(FileSearchScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.RootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.DatabasePath);
    }

    private static void ValidateSearchRequest(FileSearchScope scope, EntrySearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        if (!PathEquals(scope.RootPath, request.RootPath))
        {
            throw new ArgumentException(
                "The search request root must match the file-search scope root.",
                nameof(request));
        }

        if (request.Limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request), "Limit must be between 1 and 1000.");
        if (request.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Offset must be nonnegative.");
        if (request.MinSizeBytes is < 0 || request.MaxSizeBytes is < 0
            || request.MinSizeBytes > request.MaxSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Size bounds must be nonnegative and the minimum cannot exceed the maximum.");
        }

        ValidateDateRange(request.CreatedFromUtc, request.CreatedBeforeUtc, nameof(request));
        ValidateDateRange(request.ModifiedFromUtc, request.ModifiedBeforeUtc, nameof(request));
        if (!Enum.IsDefined(request.SortBy) || !Enum.IsDefined(request.SortDirection)
            || request.Kind is { } kind && !Enum.IsDefined(kind))
        {
            throw new ArgumentException("The search request contains an unsupported enum value.", nameof(request));
        }
    }

    private static bool PathEquals(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            comparison);
    }

    internal static string ValidateRefreshPath(string normalizedRoot, string refreshPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshPath);
        if (!Path.IsPathFullyQualified(refreshPath))
        {
            throw new ArgumentException(
                "The partial-refresh folder must be an absolute path.",
                nameof(refreshPath));
        }

        var normalizedRefreshPath = NormalizeDirectory(refreshPath);
        if (PathEquals(normalizedRoot, normalizedRefreshPath))
        {
            throw new ArgumentException(
                "Choose a subfolder below the search root for a partial refresh.",
                nameof(refreshPath));
        }

        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedRefreshPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The partial-refresh folder must be inside the selected search root.",
                nameof(refreshPath));
        }

        if (!Directory.Exists(normalizedRefreshPath))
        {
            throw new DirectoryNotFoundException(
                $"Partial-refresh folder not found: {normalizedRefreshPath}");
        }

        return normalizedRefreshPath;
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ValidateDateRange(
        DateTimeOffset? from,
        DateTimeOffset? before,
        string parameterName)
    {
        if (from is not null && before is not null && from >= before)
            throw new ArgumentException("Date lower bounds must precede upper bounds.", parameterName);
    }
}
