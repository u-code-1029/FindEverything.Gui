namespace FindEverything.Application.Indexing;

/// <summary>
/// Walks a source tree without creating or updating the persistent file index.
/// The visitor sees each directory before the service decides whether to enter it.
/// </summary>
public interface IDirectoryDiscoveryService
{
    Task<DirectoryDiscoveryReport> DiscoverAsync(
        DirectoryDiscoveryRequest request,
        Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
        IProgress<DirectoryDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record DirectoryDiscoveryRequest(string RootPath)
{
    public int MaxEntriesPerSecond { get; init; } = 2000;

    public TimeSpan DirectoryDelay { get; init; } = TimeSpan.FromMilliseconds(5);
}

public sealed record DiscoveredDirectory(
    string FullPath,
    string Name,
    string ParentPath,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc);

public enum DirectoryTraversalDecision
{
    Continue = 0,
    SkipDescendants = 1,
}

public enum DirectoryDiscoveryStatus
{
    Completed = 0,
    Partial = 1,
    Cancelled = 2,
}

public sealed record DirectoryDiscoveryProgress(
    long Entries,
    long Directories,
    long PrunedDirectories,
    long SkippedLinks,
    long ErrorCount,
    TimeSpan Elapsed);

public sealed record DirectoryDiscoveryError(string Path, string Message);

public sealed record DirectoryDiscoveryReport(
    string RootPath,
    DirectoryDiscoveryStatus Status,
    DirectoryDiscoveryProgress Progress,
    IReadOnlyList<DirectoryDiscoveryError> Errors);
