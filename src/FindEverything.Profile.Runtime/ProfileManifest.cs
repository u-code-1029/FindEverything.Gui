using System.Text.Json.Serialization;

namespace FindEverything.Profile.Runtime;

public static class ProfileManifestLimits
{
    public const long MaximumLengthBytes = 1024 * 1024;

    public const int MaximumCompositeGroupCount = 16;

    public const int MaximumStopTraversalGroupCount = 16;

    public const int MaximumRegexRuleCount = 64;

    public const int MaximumRegexPatternLength = 4096;

    public const int MaximumAggregateRegexTimeoutMilliseconds = 10_000;

    public const int MaximumExcludedDirectoryNameRuleCount = 64;

    public const int MaximumExcludedDirectoryNamePatternLength = 4096;
}

public sealed class ProfileManifest
{
    public int ContractVersion { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileKind>))]
    public ProfileKind Kind { get; set; } = ProfileKind.Assembly;

    public string? Id { get; set; }

    public string? Version { get; set; }

    public string? DisplayName { get; set; }

    public string? EntryAssembly { get; set; }

    public string? ModelType { get; set; }

    public List<ProfileFieldManifest>? Fields { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileCandidateKind>))]
    public ProfileCandidateKind CandidateKind { get; set; } = ProfileCandidateKind.Directory;

    /// <summary>
    /// Optional regular-expression rules that prune directories by leaf name during
    /// profile-aware direct discovery. The full path is never supplied to these rules.
    /// </summary>
    public List<ProfileDirectoryNameExclusionRuleManifest>? ExcludedDirectoryNameRules { get; set; }

    public List<ProfileRegexRuleManifest>? Rules { get; set; }
}

public sealed class ProfileDirectoryNameExclusionRuleManifest
{
    public string? Id { get; set; }

    public string? Pattern { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileRegexMatchMode>))]
    public ProfileRegexMatchMode MatchMode { get; set; } = ProfileRegexMatchMode.Full;

    public bool IgnoreCase { get; set; }

    public int TimeoutMilliseconds { get; set; } = 100;
}

public sealed class ProfileFieldManifest
{
    public string? FieldId { get; set; }

    public string? GroupName { get; set; }

    public List<string>? GroupNames { get; set; }

    public string? Header { get; set; }

    public int Order { get; set; }

    public bool Required { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileFieldValueKind>))]
    public ProfileFieldValueKind Kind { get; set; } = ProfileFieldValueKind.String;

    public string? ParseFormat { get; set; }

    public string? DisplayFormat { get; set; }
}

public sealed class ProfileRegexRuleManifest
{
    public string? Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PathTemplate { get; set; }

    public string? Pattern { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileRegexMatchMode>))]
    public ProfileRegexMatchMode MatchMode { get; set; } = ProfileRegexMatchMode.Full;

    public bool IgnoreCase { get; set; }

    public int TimeoutMilliseconds { get; set; } = 100;

    public List<string>? StopTraversalWhenCapturedGroups { get; set; }
}

public enum ProfileKind
{
    Assembly = 0,
    Declarative = 1,
}

public enum ProfileCandidateKind
{
    Directory = 0,
}

public enum ProfileRegexMatchMode
{
    Full = 0,
    Partial = 1,
}
