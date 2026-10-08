using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

public enum CatalogScanTraceKind
{
    Started = 0,
    DirectoryVisited = 1,
    DiscoveryError = 2,
    Completed = 3,
    Cancelled = 4,
    Failed = 5,
    DirectoryExcluded = 6,
    DirectoryExclusionIssue = 7,
}

/// <summary>
/// Describes one ordered diagnostic event from a profile-aware structured scan.
/// A persistent scan reports its lifecycle and each directory as it is evaluated
/// during the index traversal; raw file enumeration exposes aggregate progress through
/// <see cref="CatalogOperationProgress"/> instead of per-path events.
/// </summary>
public sealed record CatalogScanTraceEvent(
    Guid OperationId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    CatalogScanTraceKind Kind,
    string ProfileId,
    string ProfileDisplayName,
    string RootPath,
    string? FullPath,
    string? RelativePath,
    string? MatchInput,
    ProfileMapStatus? MappingStatus,
    string? MatchedRuleId,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<ProfileMappingIssue> Issues,
    DirectoryTraversalDecision? TraversalDecision,
    string Message);

public interface ICatalogScanTraceSink
{
    void Report(CatalogScanTraceEvent value);
}

internal sealed class NullCatalogScanTraceSink : ICatalogScanTraceSink
{
    public void Report(CatalogScanTraceEvent value)
    {
    }
}
