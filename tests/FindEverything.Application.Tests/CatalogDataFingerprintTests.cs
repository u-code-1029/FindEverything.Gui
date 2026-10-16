using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogDataFingerprintTests
{
    [Fact]
    public void Absolute_path_exclusion_rules_are_part_of_the_structured_cache_identity()
    {
        var profile = new ProfileDescriptor("sample", "1", "Sample", ProfileCandidateKind.Directory, [], []);
        var original = CatalogDataFingerprint.Create(profile, "runtime");
        var excluded = profile with
        {
            DirectoryExclusionRules = [new(0, "private", @"[\\/]private[\\/]", ProfileRegexMatchMode.Partial, true, 100)],
        };
        Assert.NotEqual(original, CatalogDataFingerprint.Create(excluded, "runtime"));
        Assert.NotEqual(CatalogDataFingerprint.Create(excluded, "runtime"),
            CatalogDataFingerprint.Create(excluded with
            { DirectoryExclusionRules = [excluded.DirectoryExclusionRules[0] with { MatchMode = ProfileRegexMatchMode.Full }] }, "runtime"));
    }

    [Fact]
    public void Interaction_settings_do_not_invalidate_data_but_read_rules_do()
    {
        var profile = new ProfileDescriptor("sample", "1.0.0", "Sample", ProfileCandidateKind.Directory, [], [])
        {
            Kind = ProfileKind.Declarative,
            TextFileFields = [new(0, "note", "Note", "note.txt", ProfileRegexMatchMode.Full, true, 100, false, 4096)]
        };
        var first = CatalogDataFingerprint.Create(profile, "runtime");
        var changed = profile with
        {
            MaxScanDepth = 2,
            SelectionMatchRules = [new() { Id = "by-id", DisplayName = "Match ID" }],
            TextFileFields = [profile.TextFileFields[0] with { EditOptions = new() { Enabled = true, CreationFileNameTemplate = "note.txt" } }]
        };
        Assert.Equal(first, CatalogDataFingerprint.Create(changed, "runtime"));
        changed = changed with { TextFileFields = [changed.TextFileFields[0] with { MaxBytes = 8192 }] };
        Assert.NotEqual(first, CatalogDataFingerprint.Create(changed, "runtime"));
    }
}
