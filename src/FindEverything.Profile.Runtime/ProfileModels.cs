using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace FindEverything.Profile.Runtime;

public enum ProfileFieldValueKind
{
    String = 0,
    Int32 = 1,
    Decimal = 2,
    DateTime = 3,
    Boolean = 4,
}

public sealed record ProfileFieldDescriptor(
    string FieldId,
    string GroupName,
    string Header,
    int Order,
    bool Required,
    ProfileFieldValueKind Kind,
    bool IsNullable,
    string? ParseFormat,
    string? DisplayFormat)
{
    public IReadOnlyList<string> GroupNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EffectiveGroupNames =>
        GroupNames.Count > 0 ? GroupNames : new[] { GroupName };
}

public sealed record ProfileRegexRuleDescriptor(
    int Order,
    string Id,
    string Pattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds)
{
    public IReadOnlyList<string> StopTraversalWhenCapturedGroups { get; init; } =
        Array.Empty<string>();
}

public sealed record ProfileDescriptor(
    string Id,
    string Version,
    string DisplayName,
    ProfileCandidateKind CandidateKind,
    ProfilePathInput PathInput,
    IReadOnlyList<ProfileFieldDescriptor> Fields,
    IReadOnlyList<ProfileRegexRuleDescriptor> Rules)
{
    public ProfileKind Kind { get; init; } = ProfileKind.Assembly;
}

public sealed record ProfilePathCandidate(string FullPath, string RelativePath);

public sealed record MappedProfileItem(
    string ProfileId,
    string FullPath,
    string RelativePath,
    string MatchedRuleId,
    object Model,
    IReadOnlyDictionary<string, object?> Values);

public enum ProfileMapStatus
{
    NoMatch = 0,
    Success = 1,
    Invalid = 2,
}

public sealed record ProfileMappingIssue(string Code, string? FieldId, string Message);

public sealed class ProfileMapResult
{
    private ProfileMapResult(
        ProfileMapStatus status,
        MappedProfileItem? item,
        IReadOnlyList<ProfileMappingIssue> issues,
        string? matchedRuleId,
        bool shouldPruneDescendants)
    {
        Status = status;
        Item = item;
        Issues = issues;
        MatchedRuleId = matchedRuleId;
        ShouldPruneDescendants = shouldPruneDescendants;
    }

    public ProfileMapStatus Status { get; }

    public MappedProfileItem? Item { get; }

    public IReadOnlyList<ProfileMappingIssue> Issues { get; }

    public string? MatchedRuleId { get; }

    public bool ShouldPruneDescendants { get; }

    public static ProfileMapResult NoMatch() =>
        new(ProfileMapStatus.NoMatch, null, Array.Empty<ProfileMappingIssue>(), null, false);

    // Keep the original one-argument factory as a real overload, rather than an
    // optional parameter, so already-compiled profile integrations retain their
    // binary call target.
    public static ProfileMapResult Success(MappedProfileItem item) =>
        Success(item, shouldPruneDescendants: false);

    public static ProfileMapResult Success(
        MappedProfileItem item,
        bool shouldPruneDescendants) =>
        new(
            ProfileMapStatus.Success,
            item,
            Array.Empty<ProfileMappingIssue>(),
            item.MatchedRuleId,
            shouldPruneDescendants);

    // Preserve the original public signature for binary compatibility.
    public static ProfileMapResult Invalid(IEnumerable<ProfileMappingIssue> issues) =>
        Invalid(issues, matchedRuleId: null, shouldPruneDescendants: false);

    public static ProfileMapResult Invalid(
        IEnumerable<ProfileMappingIssue> issues,
        string? matchedRuleId,
        bool shouldPruneDescendants) =>
        new(
            ProfileMapStatus.Invalid,
            null,
            Array.AsReadOnly(issues.ToArray()),
            matchedRuleId,
            shouldPruneDescendants);
}

public enum ProfileDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

public sealed record ProfileDiagnostic(
    ProfileDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Detail = null);

public enum ProfilePluginStatus
{
    Loaded = 0,
    Disabled = 1,
}

public sealed record ProfilePluginReport(
    string SourceDirectory,
    string? ProfileId,
    string? DisplayName,
    ProfilePluginStatus Status,
    IReadOnlyList<ProfileDiagnostic> Diagnostics);

public interface ILoadedProfile
{
    ProfileDescriptor Descriptor { get; }

    ProfileMapResult Map(ProfilePathCandidate candidate);
}

public sealed class ProfileCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, ILoadedProfile> _profilesById;

    public ProfileCatalogSnapshot(
        IEnumerable<ILoadedProfile> profiles,
        IEnumerable<ProfilePluginReport> reports,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(reports);

        var profileArray = profiles.ToArray();
        var reportArray = reports.ToArray();
        var profilesById = profileArray.ToDictionary(
            static profile => profile.Descriptor.Id,
            StringComparer.OrdinalIgnoreCase);

        Profiles = Array.AsReadOnly(profileArray);
        Reports = Array.AsReadOnly(reportArray);
        CreatedAt = createdAt;
        _profilesById = new ReadOnlyDictionary<string, ILoadedProfile>(profilesById);
    }

    public static ProfileCatalogSnapshot Empty { get; } = new(
        Array.Empty<ILoadedProfile>(),
        Array.Empty<ProfilePluginReport>(),
        DateTimeOffset.MinValue);

    public IReadOnlyList<ILoadedProfile> Profiles { get; }

    public IReadOnlyList<ProfilePluginReport> Reports { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool TryGetProfile(
        string profileId,
        [NotNullWhen(true)] out ILoadedProfile? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return _profilesById.TryGetValue(profileId, out profile);
    }
}

public interface IProfileCatalog
{
    ProfileCatalogSnapshot Current { get; }

    event EventHandler<ProfileCatalogChangedEventArgs>? Changed;
}

public sealed class ProfileCatalogChangedEventArgs(
    ProfileCatalogSnapshot previous,
    ProfileCatalogSnapshot current) : EventArgs
{
    public ProfileCatalogSnapshot Previous { get; } = previous;

    public ProfileCatalogSnapshot Current { get; } = current;
}

public interface IProfileCatalogPublisher
{
    void Publish(ProfileCatalogSnapshot snapshot);
}

public interface IProfileResolver
{
    bool TryResolve(string profileId, [NotNullWhen(true)] out ILoadedProfile? profile);
}

public interface IProfilePluginLoader
{
    Task<ProfileCatalogSnapshot> LoadAsync(CancellationToken cancellationToken = default);
}
