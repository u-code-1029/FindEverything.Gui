using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

// The namespace is retained for source compatibility; these contracts belong to
// the dependency-free Abstractions assembly, not the runtime implementation.
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
    UserInput = 2,
    ChildDirectoryExists = 3,
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

    public string? Placeholder { get; init; }

    public string? DefaultValue { get; init; }

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
    long MaxBytes)
{
    public ProfileTextFileEditOptions? EditOptions { get; init; }
}

public sealed record ProfileChildDirectoryFieldDescriptor(
    int Order, string FieldId, string Header, string DirectoryName, bool IgnoreCase);

public sealed record ProfileUserInputFieldDescriptor(
    int Order,
    string FieldId,
    string Header,
    string? Placeholder,
    string? DefaultValue);

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

public sealed record ProfileDirectoryExclusionRuleDescriptor(
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

    public IReadOnlyList<ProfileDirectoryExclusionRuleDescriptor>
        DirectoryExclusionRules { get; init; } =
            Array.Empty<ProfileDirectoryExclusionRuleDescriptor>();

    public IReadOnlyList<ProfileTextFileFieldDescriptor> TextFileFields { get; init; } =
        Array.Empty<ProfileTextFileFieldDescriptor>();

    public IReadOnlyList<ProfileUserInputFieldDescriptor> UserInputFields { get; init; } =
        Array.Empty<ProfileUserInputFieldDescriptor>();

    public IReadOnlyList<ProfileChildDirectoryFieldDescriptor> ChildDirectoryFields { get; init; } =
        Array.Empty<ProfileChildDirectoryFieldDescriptor>();

    public int MaxScanDepth { get; init; } = 256;

    public IReadOnlyList<ProfileSelectionMatchRuleManifest> SelectionMatchRules { get; init; } =
        Array.Empty<ProfileSelectionMatchRuleManifest>();
}

public sealed record ProfilePathCandidate
{
    public ProfilePathCandidate(string absolutePath)
    {
        ValidateAbsolutePath(absolutePath);
        AbsolutePath = absolutePath;
    }

    /// <summary>Shared, allocation-free validation for canonical absolute profile inputs.</summary>
    public static void ValidateAbsolutePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException(
                "프로필 입력은 드라이브 또는 UNC 루트를 포함한 절대 경로여야 합니다.",
                nameof(absolutePath));
        }
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

    public IReadOnlyDictionary<string, ProfileFieldResourceInfo> Resources { get; init; } =
        new ReadOnlyDictionary<string, ProfileFieldResourceInfo>(new Dictionary<string, ProfileFieldResourceInfo>());

    public IReadOnlyDictionary<string, string> FieldErrors { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

public enum ProfileFieldResourceState { Unknown, Missing, Present, Error }

public sealed record ProfileFieldResourceInfo(ProfileFieldResourceState State,
    string? FileName = null, string? EncodingName = null, bool? HasBom = null,
    string? NewLine = null, string? Version = null);

public enum ProfileMapStatus
{
    NoMatch = 0,
    Success = 1,
    Invalid = 2,
}

public sealed record ProfileMappingIssue(string Code, string? FieldId, string Message);

public sealed record ProfileDirectoryExclusionResult(
    bool IsExcluded,
    string? MatchedRuleId,
    IReadOnlyList<ProfileMappingIssue> Issues)
{
    public static ProfileDirectoryExclusionResult NotExcluded() =>
        new(false, null, Array.Empty<ProfileMappingIssue>());

    public static ProfileDirectoryExclusionResult Excluded(string matchedRuleId) =>
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

/// <summary>The outcome of reading one text column without mapping other fields.</summary>
public sealed record ProfileTextColumnReadResult(
    string? Value,
    IReadOnlyList<ProfileMappingIssue> Issues)
{
    public ProfileFieldResourceInfo? Resource { get; init; }
}

public interface ILoadedProfile
{
    ProfileDescriptor Descriptor { get; }

    /// <summary>Identifies implementation code used by cached parsing results.</summary>
    string CacheIdentity => GetType().FullName + "|" + GetType().Module.ModuleVersionId.ToString("N");

    ProfileMapResult Map(ProfilePathCandidate candidate);

    /// <summary>Cooperative cancellation; legacy plug-ins can only be checked before and after their call.</summary>
    ProfileMapResult Map(ProfilePathCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Map(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// Reads only the requested immediate text-file field. It must not remap the
    /// path, evaluate other columns, or visit descendant folders.
    /// </summary>
    ProfileTextColumnReadResult ReadTextColumn(string fullPath, string fieldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        return new(null, [new ProfileMappingIssue(
            "text_column_refresh_unsupported", fieldId,
            "이 프로필은 텍스트 컬럼만 다시 읽는 기능을 지원하지 않습니다.")]);
    }

    /// <summary>
    /// Evaluates one canonical absolute directory path, including its drive/UNC root
    /// and all parent segments. The scanner separately protects explicit starting roots.
    /// </summary>
    ProfileDirectoryExclusionResult EvaluateDirectoryPath(string absolutePath)
    {
        ProfilePathCandidate.ValidateAbsolutePath(absolutePath);
        return ProfileDirectoryExclusionResult.NotExcluded();
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
