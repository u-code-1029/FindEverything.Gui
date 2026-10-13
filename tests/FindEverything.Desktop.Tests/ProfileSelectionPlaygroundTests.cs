using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileSelectionPlaygroundTests
{
    [Fact]
    public async Task Rule_preview_uses_shared_parser_without_mapping_paths_or_scanning()
    {
        var profile = new FrozenSampleProfile(); var rule = Rule();
        var result = await ProfileSelectionPlayground.TestAsync(profile, null, rule, "alpha\nbeta", "en-US");
        Assert.Equal(2, result.ParsedRows); Assert.Null(result.MatchSummary); Assert.Equal(2, result.PreviewRows.Count);
    }
    [Fact]
    public async Task Match_test_uses_full_typed_sample_and_alias_definitions_without_filesystem_map()
    {
        var profile = new FrozenSampleProfile(); var sample = new MappedProfileItem("sample", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "offline-playground-sample"),
            "all", new object(), new Dictionary<string, object?> { ["alias"] = "A" });
        var result = await ProfileSelectionPlayground.TestAsync(profile, sample, Rule(), "홍길동\nunmatched", "ko-KR");
        Assert.Equal(1, result.MatchSummary!.MatchingItems); Assert.Equal(1, result.MatchSummary.MatchedInputRows);
        Assert.Equal(1, result.MatchSummary.UnmatchedInputRows);
    }
    private static ProfileSelectionMatchRuleManifest Rule() => new()
    { Id = "names", DisplayName = "Names", InputKind = ProfileSelectionInputKind.TextLines, Columns = [new() { FieldId = "alias" }] };
    private sealed class FrozenSampleProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("sample", "1", "Sample", ProfileCandidateKind.Directory,
            [new("alias", "alias", "Name", 0, true, ProfileFieldValueKind.String, false, null, null)
                { ValueMappings = new Dictionary<string, string> { ["A"] = "홍길동" } }], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => throw new InvalidOperationException("Preview must not remap or perform file IO.");
    }
}
