using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfileDefinitionCompilerTests
{
    [Fact]
    public void CaptureFieldAttribute_keeps_legacy_constructor_signature()
    {
        var constructor = typeof(CaptureFieldAttribute).GetConstructor(
            [typeof(string), typeof(string)]);

        Assert.NotNull(constructor);
        var attribute = Assert.IsType<CaptureFieldAttribute>(
            constructor.Invoke(["year", "year"]));
        Assert.Equal("year", attribute.GroupName);
        Assert.Equal(["year"], attribute.GroupNames);
    }

    [Fact]
    public void ProfileMapResult_keeps_legacy_factory_signatures()
    {
        Assert.NotNull(typeof(ProfileMapResult).GetMethod(
            nameof(ProfileMapResult.Success),
            [typeof(MappedProfileItem)]));
        Assert.NotNull(typeof(ProfileMapResult).GetMethod(
            nameof(ProfileMapResult.Invalid),
            [typeof(IEnumerable<ProfileMappingIssue>)]));
    }

    [Theory]
    [InlineData(@"2026\0521_Project", "Project")]
    [InlineData("2026/0521_Project", "Project")]
    public void CompositeDateTime_concatenates_ordered_groups(
        string path,
        string expectedName)
    {
        using var provider = BuildProvider();
        var profile = Compile(provider, CompositeDateManifest());

        var result = profile.Map(new ProfilePathCandidate(path, path));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Equal(new DateTime(2026, 5, 21), result.Item!.Values["captured-on"]);
        Assert.Equal(expectedName, result.Item.Values["name"]);
        Assert.Equal("default", result.MatchedRuleId);
        Assert.True(result.ShouldPruneDescendants);
    }

    [Fact]
    public void CompositeDateTime_invalid_value_keeps_terminal_match_metadata()
    {
        using var provider = BuildProvider();
        var profile = Compile(provider, CompositeDateManifest());

        var result = profile.Map(new ProfilePathCandidate(
            "2026/1332_Project",
            "2026/1332_Project"));

        Assert.Equal(ProfileMapStatus.Invalid, result.Status);
        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "capture_conversion_failed"
                && issue.FieldId == "captured-on");
        Assert.Equal("default", result.MatchedRuleId);
        Assert.True(result.ShouldPruneDescendants);
    }

    [Fact]
    public void OptionalCompositeField_all_missing_maps_to_null()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest(required: false);
        manifest.Rules![0].Pattern =
            @"(?:(?<year>\d{4})[\\/](?<monthDay>\d{4})_)?(?<name>[^\\/]+)";
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate("Project", "Project"));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Null(result.Item!.Values["captured-on"]);
        Assert.False(result.ShouldPruneDescendants);
    }

    [Fact]
    public void OptionalCompositeField_partial_capture_is_invalid()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest(required: false);
        manifest.Rules![0].Pattern =
            @"(?:(?<year>\d{4})(?:[\\/](?<monthDay>\d{4}))?_)?(?<name>[^\\/]+)";
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate(
            "2026_Project",
            "2026_Project"));

        Assert.Equal(ProfileMapStatus.Invalid, result.Status);
        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "composite_capture_incomplete"
                && issue.FieldId == "captured-on");
        Assert.False(result.ShouldPruneDescendants);
    }

    [Fact]
    public void Validation_rejects_conflicting_group_sources()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Fields![0].GroupName = "legacyDate";

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "capture_group_sources_conflict");
    }

    [Fact]
    public void Validation_requires_parse_format_for_composite_date()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Fields![0].ParseFormat = null;

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "capture_composite_datetime_parse_format_missing");
    }

    [Fact]
    public void Legacy_single_group_profile_remains_compatible()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Fields![0].GroupNames = null;
        manifest.Fields[0].GroupName = "date";
        manifest.Rules![0].Pattern = @"(?<date>\d{8})_(?<name>[^\\/]+)";
        manifest.Rules[0].StopTraversalWhenCapturedGroups = null;
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate(
            "20260521_Project",
            "20260521_Project"));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Equal(new DateTime(2026, 5, 21), result.Item!.Values["captured-on"]);
        Assert.False(result.ShouldPruneDescendants);
    }

    [Fact]
    public void Terminal_groups_must_all_have_non_whitespace_captures()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest(required: false);
        manifest.Rules![0].Pattern =
            @"(?:(?<year>\d{4})[\\/](?<monthDay>\d{4})_)?(?<name>[^\\/_]+)(?:_(?<leaf>\S+))?";
        manifest.Rules[0].StopTraversalWhenCapturedGroups = ["name", "leaf"];
        var profile = Compile(provider, manifest);

        var branch = profile.Map(new ProfilePathCandidate("Project", "Project"));
        var leaf = profile.Map(new ProfilePathCandidate("Project_done", "Project_done"));

        Assert.Equal(ProfileMapStatus.Success, branch.Status);
        Assert.False(branch.ShouldPruneDescendants);
        Assert.Equal(ProfileMapStatus.Success, leaf.Status);
        Assert.True(leaf.ShouldPruneDescendants);
    }

    [Fact]
    public void Validation_rejects_undefined_terminal_group()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].StopTraversalWhenCapturedGroups = ["missing"];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "stop_traversal_group_not_defined");
    }

    [Fact]
    public void Regex_timeout_never_prunes_descendants()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].Pattern =
            @"(?<year>(a+)+)(?<monthDay>\d{4})_(?<name>[^\\/]+)";
        manifest.Rules[0].TimeoutMilliseconds = 1;
        var profile = Compile(provider, manifest);
        var path = new string('a', 100_000) + "!";

        var result = profile.Map(new ProfilePathCandidate(path, path));

        Assert.Equal(ProfileMapStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, static issue => issue.Code == "regex_timeout");
        Assert.Null(result.MatchedRuleId);
        Assert.False(result.ShouldPruneDescendants);
    }

    [Theory]
    [InlineData(false, "capture_group_names_limit_exceeded")]
    [InlineData(true, "stop_traversal_group_limit_exceeded")]
    public void Validation_limits_configured_group_lists(
        bool terminalGroups,
        string expectedCode)
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        var maximumCount = terminalGroups
            ? ProfileManifestLimits.MaximumStopTraversalGroupCount
            : ProfileManifestLimits.MaximumCompositeGroupCount;
        var excessiveGroups = Enumerable
            .Range(1, maximumCount + 1)
            .Select(static number => $"group{number}")
            .ToList();
        if (terminalGroups)
        {
            manifest.Rules![0].StopTraversalWhenCapturedGroups = excessiveGroups;
        }
        else
        {
            manifest.Fields![0].GroupNames = excessiveGroups;
        }

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            diagnostic => diagnostic.Code == expectedCode);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddProfileRuntime();
        return services.BuildServiceProvider();
    }

    private static ILoadedProfile Compile(
        IServiceProvider provider,
        ProfileManifest manifest)
    {
        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);
        Assert.True(
            review.IsValid,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return review.Profile!;
    }

    private static ProfileManifest CompositeDateManifest(bool required = true) =>
        new()
        {
            ContractVersion = 1,
            Kind = ProfileKind.Declarative,
            Id = "composite-date",
            Version = "1.0.0",
            DisplayName = "Composite date",
            CandidateKind = ProfileCandidateKind.Directory,
            PathInput = ProfilePathInput.Relative,
            Fields =
            [
                new ProfileFieldManifest
                {
                    FieldId = "captured-on",
                    GroupNames = ["year", "monthDay"],
                    Header = "날짜",
                    Order = 10,
                    Required = required,
                    Kind = ProfileFieldValueKind.DateTime,
                    ParseFormat = "yyyyMMdd",
                    DisplayFormat = "yyyy-MM-dd",
                },
                new ProfileFieldManifest
                {
                    FieldId = "name",
                    GroupName = "name",
                    Header = "이름",
                    Order = 20,
                    Required = true,
                    Kind = ProfileFieldValueKind.String,
                },
            ],
            Rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "default",
                    Pattern = @"(?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<name>[^\\/]+)",
                    MatchMode = ProfileRegexMatchMode.Full,
                    TimeoutMilliseconds = 100,
                    StopTraversalWhenCapturedGroups = ["year", "monthDay"],
                },
            ],
        };
}
