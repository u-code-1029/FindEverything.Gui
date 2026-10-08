using FindEverything.Application.Indexing;
using Engine = global::FindEverything.Engine;

namespace FindEverything.Infrastructure.FindEverything;

internal sealed class DirectoryDiscoveryService : IDirectoryDiscoveryService
{
    public async Task<DirectoryDiscoveryReport> DiscoverAsync(
        DirectoryDiscoveryRequest request,
        Func<DiscoveredDirectory, DirectoryTraversalDecision> visitDirectory,
        IProgress<DirectoryDiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(visitDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);

        var scanner = new Engine.FileSystemMetadataScanner();
        var engineProgress = progress is null
            ? null
            : new ProgressAdapter(progress);
        var report = await scanner.DiscoverDirectoriesAsync(
            new Engine.DirectoryDiscoveryRequest(request.RootPath)
            {
                Options = new Engine.ScanOptions
                {
                    MaxEntriesPerSecond = request.MaxEntriesPerSecond,
                    DirectoryDelay = request.DirectoryDelay,
                },
            },
            candidate => visitDirectory(new DiscoveredDirectory(
                    candidate.FullPath,
                    candidate.Name,
                    candidate.ParentPath
                        ?? Path.GetDirectoryName(candidate.FullPath)
                        ?? candidate.FullPath,
                    candidate.CreatedUtc,
                    candidate.ModifiedUtc)) switch
            {
                DirectoryTraversalDecision.Continue =>
                    Engine.DirectoryTraversalDecision.Continue,
                DirectoryTraversalDecision.SkipDescendants =>
                    Engine.DirectoryTraversalDecision.SkipDescendants,
                DirectoryTraversalDecision.ExcludeSubtree =>
                    Engine.DirectoryTraversalDecision.ExcludeSubtree,
                var decision => throw new ArgumentOutOfRangeException(
                    nameof(visitDirectory),
                    decision,
                    "Unknown directory traversal decision."),
            },
            engineProgress,
            cancellationToken).ConfigureAwait(false);

        return new DirectoryDiscoveryReport(
            report.RootPath,
            ToApplicationStatus(report.Status),
            ToApplicationProgress(report.Progress),
            report.Errors
                .Select(static error => new DirectoryDiscoveryError(error.Path, error.Message))
                .ToArray());
    }

    private static DirectoryDiscoveryStatus ToApplicationStatus(Engine.ScanStatus status) =>
        status switch
        {
            Engine.ScanStatus.Completed => DirectoryDiscoveryStatus.Completed,
            Engine.ScanStatus.Partial or Engine.ScanStatus.Deferred => DirectoryDiscoveryStatus.Partial,
            Engine.ScanStatus.Cancelled => DirectoryDiscoveryStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown discovery status."),
        };

    private static DirectoryDiscoveryProgress ToApplicationProgress(
        Engine.DirectoryDiscoveryProgress progress) =>
        new(
            progress.Entries,
            progress.Directories,
            progress.PrunedDirectories,
            progress.SkippedLinks,
            progress.ErrorCount,
            progress.Elapsed);

    private sealed class ProgressAdapter(IProgress<DirectoryDiscoveryProgress> progress) :
        IProgress<Engine.DirectoryDiscoveryProgress>
    {
        public void Report(Engine.DirectoryDiscoveryProgress value) =>
            progress.Report(ToApplicationProgress(value));
    }
}
