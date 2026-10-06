using System.ComponentModel.DataAnnotations;
using FindEverything.Profile.Abstractions;

namespace FindEverything.Profile.Runtime;

public sealed class PluginDiscoveryOptions
{
    public const string SectionName = "Plugins";

    [Required]
    public string ProfilesDirectory { get; set; } = "Profiles";

    public string? UserProfilesDirectory { get; set; }

    [Range(1, int.MaxValue)]
    public int ContractMajor { get; set; } = ProfileContract.CurrentMajor;
}
