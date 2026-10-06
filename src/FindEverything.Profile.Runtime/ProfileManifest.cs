using System.Text.Json.Serialization;

namespace FindEverything.Profile.Runtime;

public sealed class ProfileManifest
{
    public int ContractVersion { get; set; }

    public string? Id { get; set; }

    public string? Version { get; set; }

    public string? DisplayName { get; set; }

    public string? EntryAssembly { get; set; }

    public string? ModelType { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileCandidateKind>))]
    public ProfileCandidateKind CandidateKind { get; set; } = ProfileCandidateKind.Directory;

    [JsonConverter(typeof(JsonStringEnumConverter<ProfilePathInput>))]
    public ProfilePathInput PathInput { get; set; } = ProfilePathInput.Relative;

    public List<ProfileRegexRuleManifest>? Rules { get; set; }
}

public sealed class ProfileRegexRuleManifest
{
    public string? Id { get; set; }

    public string? Pattern { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ProfileRegexMatchMode>))]
    public ProfileRegexMatchMode MatchMode { get; set; } = ProfileRegexMatchMode.Full;

    public bool IgnoreCase { get; set; }

    public int TimeoutMilliseconds { get; set; } = 100;
}

public enum ProfileCandidateKind
{
    Directory = 0,
}

public enum ProfilePathInput
{
    Relative = 0,
    Full = 1,
}

public enum ProfileRegexMatchMode
{
    Full = 0,
    Partial = 1,
}
