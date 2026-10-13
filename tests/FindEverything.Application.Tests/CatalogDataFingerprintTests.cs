using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogDataFingerprintTests
{
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
