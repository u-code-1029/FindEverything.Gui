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
}

/// <summary>
/// Describes one ordered diagnostic event from a direct profile-aware directory scan.
/// Persistent-index operations do not publish these events.
/// </summary>
public sealed record CatalogScanTraceEvent(
    Guid OperationId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    CatalogScanTraceKind Kind,
    string ProfileId,
    string ProfileDisplayName,
    string RootPath,
    ProfilePathInput PathInput,
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
