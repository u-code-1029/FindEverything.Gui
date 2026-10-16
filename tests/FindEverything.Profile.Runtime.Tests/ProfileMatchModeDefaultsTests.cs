using System.Text.Json;
using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfileMatchModeDefaultsTests
{
    [Fact]
    public void Missing_match_modes_default_to_partial_but_explicit_full_is_preserved()
    {
        Assert.Equal(ProfileRegexMatchMode.Partial, new ProfileRegexRuleManifest().MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Partial, new ProfileDirectoryExclusionRuleManifest().MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Partial, new ProfileTextFileFieldManifest().MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Partial, JsonSerializer.Deserialize<ProfileRegexRuleManifest>("{}")!.MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Partial, JsonSerializer.Deserialize<ProfileDirectoryExclusionRuleManifest>("{}")!.MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Partial, JsonSerializer.Deserialize<ProfileTextFileFieldManifest>("{}")!.MatchMode);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string explicitFull = """{"matchMode":"Full"}""";
        Assert.Equal(ProfileRegexMatchMode.Full, JsonSerializer.Deserialize<ProfileRegexRuleManifest>(explicitFull, options)!.MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Full, JsonSerializer.Deserialize<ProfileDirectoryExclusionRuleManifest>(explicitFull, options)!.MatchMode);
        Assert.Equal(ProfileRegexMatchMode.Full, JsonSerializer.Deserialize<ProfileTextFileFieldManifest>(explicitFull, options)!.MatchMode);
    }

    [Theory]
    [InlineData(false, ProfileMapStatus.Success)]
    [InlineData(true, ProfileMapStatus.NoMatch)]
    public void Default_rule_captures_within_an_absolute_path_while_explicit_full_requires_the_entire_path(
        bool useFull, ProfileMapStatus expected)
    {
        using var provider = new ServiceCollection().AddProfileRuntime().BuildServiceProvider();
        var rule = new ProfileRegexRuleManifest { Id = "project", Pattern = @"PRJ-(?<number>\d+)" };
        if (useFull) rule.MatchMode = ProfileRegexMatchMode.Full;
        var manifest = new ProfileManifest
        {
            ContractVersion = ProfileContract.CurrentMajor, Kind = ProfileKind.Declarative,
            Id = "partial-default", Version = "1.0.0", DisplayName = "Partial default",
            CandidateKind = ProfileCandidateKind.Directory,
            Fields = [new() { FieldId = "number", GroupName = "number", Header = "Project", Kind = ProfileFieldValueKind.Int32, Required = true }],
            Rules = [rule],
            DirectoryExclusionRules = [new() { Id = "cache", Pattern = "cache" }],
        };
        var review = provider.GetRequiredService<IProfileDefinitionCompiler>().Validate(manifest);
        Assert.True(review.IsValid, string.Join("; ", review.Diagnostics.Select(d => d.Message)));
        var result = review.Profile!.Map(new ProfilePathCandidate(Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "projects", "PRJ-42_release"))));

        Assert.Equal(expected, result.Status);
        if (!useFull) Assert.Equal(42, result.Item!.Values["number"]);
        Assert.True(review.Profile.EvaluateDirectoryPath(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-cache-backup", "child"))).IsExcluded);
        Assert.False(review.Profile.EvaluateDirectoryPath(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project"))).IsExcluded);
    }
}
