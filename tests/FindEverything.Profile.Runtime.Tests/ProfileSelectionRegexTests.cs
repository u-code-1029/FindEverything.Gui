using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfileSelectionRegexTests
{
    [Fact]
    public void SeparatePatternsDoNotRequireSharedPatternAndGroupsResolveAgainstTheirOwnPattern()
    {
        var rule = Rule();
        Assert.Empty(Validate(rule));
        rule.Columns[1].RegexGroup = "id";
        Assert.Contains(Validate(rule), error => error.Contains("group 'id'"));
    }

    [Fact]
    public void EveryRegexMappingNeedsItsOwnOrSharedPattern()
    {
        var rule = Rule(); rule.Columns[1].RegexPattern = null;
        Assert.Contains(Validate(rule), error => error.Contains("needs a regex pattern"));
        rule.RegexPattern = @"Name=(?<name>\w+)";
        Assert.Empty(Validate(rule));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("too-long")]
    public void InvalidColumnPatternsAreRejectedBeforeImport(string pattern)
    {
        var rule = Rule(); rule.Columns[0].RegexPattern = pattern == "too-long" ? new string('x', 4097) : pattern;
        Assert.NotEmpty(Validate(rule));
    }

    [Fact]
    public void TableCanGateWholeMatchButCannotExtractAGroupWithoutAPattern()
    {
        var rule = Rule(); rule.InputKind = ProfileSelectionInputKind.Table;
        rule.HasHeaderRow = false;
        rule.Columns[0].InputColumnIndex = 0; rule.Columns[1].InputColumnIndex = 1;
        rule.Columns[1].RegexGroup = null;
        Assert.Empty(Validate(rule));
        rule.Columns[0].RegexPattern = null;
        Assert.Contains(Validate(rule), error => error.Contains("needs a regex pattern to extract"));
    }

    private static IReadOnlyList<string> Validate(ProfileSelectionMatchRuleManifest rule) =>
        ProfileSelectionMatchRules.Validate([rule], ["id", "name"]);
    private static ProfileSelectionMatchRuleManifest Rule() => new()
    {
        Id = "and", DisplayName = "AND", InputKind = ProfileSelectionInputKind.Regex,
        Columns = [new() { FieldId = "id", RegexPattern = @"ID=(?<id>\d+)", RegexGroup = "id" },
            new() { FieldId = "name", RegexPattern = @"Name=(?<name>\w+)", RegexGroup = "name" }],
    };
}
