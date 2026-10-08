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

public enum ProfileFieldSourceKind
{
    RegexCapture = 0,
    TextFileContent = 1,
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

    public ProfileFieldSourceKind SourceKind { get; init; } =
        ProfileFieldSourceKind.RegexCapture;

    /// <summary>
    /// Exact raw-string to display-string mappings. They are presentation metadata;
    /// parsing, model creation, filtering by raw data, and profile logic keep the
    /// captured value unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string> ValueMappings { get; init; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));

    public bool HasValueMappings => ValueMappings.Count > 0;

    public IReadOnlyList<string> EffectiveGroupNames =>
        GroupNames.Count > 0
            ? GroupNames
            : string.IsNullOrWhiteSpace(GroupName)
                ? Array.Empty<string>()
                : new[] { GroupName };
}

public sealed record ProfileTextFileFieldDescriptor(
    int Order,
    string FieldId,
    string Header,
    string FileNamePattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds,
    bool Required,
    long MaxBytes);

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

public sealed record ProfileDirectoryNameExclusionRuleDescriptor(
    int Order,
    string Id,
    string Pattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds);

public sealed record ProfileDescriptor(
    string Id,
    string Version,
    string DisplayName,
    ProfileCandidateKind CandidateKind,
    IReadOnlyList<ProfileFieldDescriptor> Fields,
    IReadOnlyList<ProfileRegexRuleDescriptor> Rules)
{
    public ProfileKind Kind { get; init; } = ProfileKind.Assembly;

    public IReadOnlyList<ProfileDirectoryNameExclusionRuleDescriptor>
        ExcludedDirectoryNameRules { get; init; } =
            Array.Empty<ProfileDirectoryNameExclusionRuleDescriptor>();

    public IReadOnlyList<ProfileTextFileFieldDescriptor> TextFileFields { get; init; } =
        Array.Empty<ProfileTextFileFieldDescriptor>();
}

public sealed record ProfilePathCandidate
{
    public ProfilePathCandidate(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException(
                "프로필 입력은 드라이브 또는 UNC 루트를 포함한 절대 경로여야 합니다.",
                nameof(absolutePath));
        }

        AbsolutePath = absolutePath;
    }

    public string AbsolutePath { get; }
}

public sealed record MappedProfileItem(
    string ProfileId,
    string FullPath,
    string MatchedRuleId,
    object Model,
    IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>
    /// Values prepared for display. This differs from <see cref="Values"/> only
    /// where a field declares an exact value mapping.
    /// </summary>
    public IReadOnlyDictionary<string, object?> DisplayValues { get; init; } = Values;
}

public enum ProfileMapStatus
{
    NoMatch = 0,
    Success = 1,
    Invalid = 2,
}

public sealed record ProfileMappingIssue(string Code, string? FieldId, string Message);

public sealed record ProfileDirectoryNameExclusionResult(
    bool IsExcluded,
    string? MatchedRuleId,
    IReadOnlyList<ProfileMappingIssue> Issues)
{
    public static ProfileDirectoryNameExclusionResult NotExcluded() =>
        new(false, null, Array.Empty<ProfileMappingIssue>());

    public static ProfileDirectoryNameExclusionResult Excluded(string matchedRuleId) =>
        new(true, matchedRuleId, Array.Empty<ProfileMappingIssue>());
}

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

    /// <summary>
    /// Evaluates only one directory's leaf name. Callers must not pass a full path.
    /// Existing binary profiles that do not define exclusion rules remain opt-in and
    /// therefore never exclude a directory.
    /// </summary>
    ProfileDirectoryNameExclusionResult EvaluateDirectoryName(string directoryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryName);
        return ProfileDirectoryNameExclusionResult.NotExcluded();
    }
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
