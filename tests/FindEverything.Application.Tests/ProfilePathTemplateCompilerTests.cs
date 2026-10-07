using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FindEverything.Application.Profiles;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class ProfilePathTemplateCompilerTests
{
    private readonly ProfilePathTemplateCompiler _compiler = new();

    [Fact]
    public void Compile_escapes_literals_and_emits_portable_type_patterns()
    {
        var fields = new[]
        {
            Field("name", "customer", ProfileFieldValueKind.String),
            Field("year", "year", ProfileFieldValueKind.Int32),
            Field("amount", "amount", ProfileFieldValueKind.Decimal),
            Field("captured-on", "capturedOn", ProfileFieldValueKind.DateTime),
            Field("approved", "approved", ProfileFieldValueKind.Boolean),
        };

        var result = _compiler.Compile(
            "Clients.v1/{name}/{year}/{amount}/{captured-on}/{approved}/{{done}}",
            fields);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            @"Clients\.v1[\\/](?<customer>[^\\/]+)[\\/](?<year>[+-]?\d+)[\\/](?<amount>[+-]?\d+(?:\.\d+)?)[\\/](?<capturedOn>[^\\/]+)[\\/](?<approved>(?:true|false))[\\/]\{done}",
            result.Pattern);

        var regex = FullMatch(result.Pattern!);
        Assert.Matches(regex, "Clients.v1/Acme/2026/-12.50/20261008/true/{done}");
        Assert.Matches(regex, @"Clients.v1\Acme\+2026\12\2026-10-08\false\{done}");
        Assert.DoesNotMatch(regex, "ClientsXv1/Acme/2026/12/20261008/true/{done}");
    }

    [Fact]
    public void Compile_nests_trailing_optional_segments()
    {
        var fields = new[]
        {
            Field("project", "project", ProfileFieldValueKind.String),
            Field("revision", "revision", ProfileFieldValueKind.Int32, required: false),
            Field("approved", "approved", ProfileFieldValueKind.Boolean, required: false),
        };

        var result = _compiler.Compile(
            "Projects/{project}/Rev-{revision?}/{approved?}",
            fields);

        Assert.True(result.IsValid);
        Assert.Equal(
            @"Projects[\\/](?<project>[^\\/]+)(?:[\\/]Rev-(?<revision>[+-]?\d+)(?:[\\/](?<approved>(?:true|false)))?)?",
            result.Pattern);

        var regex = FullMatch(result.Pattern!);
        Assert.Matches(regex, "Projects/Alpha");
        Assert.Matches(regex, @"Projects\Alpha\Rev-3");
        Assert.Matches(regex, "Projects/Alpha/Rev-3/true");
        Assert.DoesNotMatch(regex, "Projects/Alpha/true");
        Assert.DoesNotMatch(regex, "Projects/Alpha/Rev-3/true/extra");
    }

    [Theory]
    [InlineData("Clients/{name}")]
    [InlineData(@"Clients\{name}")]
    public void Compile_accepts_either_template_path_separator(string template)
    {
        var result = _compiler.Compile(
            template,
            [Field("name", "name", ProfileFieldValueKind.String)]);

        Assert.True(result.IsValid);
        Assert.Equal(@"Clients[\\/](?<name>[^\\/]+)", result.Pattern);
    }

    [Fact]
    public void Compile_emits_ordered_component_groups_for_a_composite_field()
    {
        var fields = new[]
        {
            CompositeField(
                "captured-on",
                ["year", "monthDay"],
                ProfileFieldValueKind.DateTime),
            Field("name", "name", ProfileFieldValueKind.String),
        };

        var result = _compiler.Compile(
            "{captured-on@year}/{captured-on@monthDay}_{name}",
            fields);

        Assert.True(result.IsValid);
        Assert.Equal(
            @"(?<year>[^\\/]+)[\\/](?<monthDay>[^\\/]+)_(?<name>[^\\/]+)",
            result.Pattern);
        var regex = FullMatch(result.Pattern!);
        Assert.Matches(regex, @"2026\0521_Project");
        Assert.Matches(regex, "2026/0521_Project");
    }

    [Theory]
    [InlineData("{captured-on}", "template_composite_field_requires_component")]
    [InlineData(
        "{captured-on@year}/{captured-on@year}",
        "template_component_duplicate")]
    [InlineData("{captured-on@missing}", "template_component_unknown")]
    [InlineData("{captured-on@year}", "template_composite_field_incomplete")]
    public void Compile_validates_composite_component_tokens(
        string template,
        string expectedCode)
    {
        var result = _compiler.Compile(
            template,
            [
                CompositeField(
                    "captured-on",
                    ["year", "monthDay"],
                    ProfileFieldValueKind.DateTime),
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Compile_rejects_groupName_and_groupNames_together()
    {
        var field = Field("captured-on", "date", ProfileFieldValueKind.DateTime);
        field.GroupNames = ["year", "monthDay"];

        var result = _compiler.Compile("{captured-on}", [field]);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "template_group_sources_conflict");
    }

    [Fact]
    public void Compile_limits_composite_group_count()
    {
        var groupNames = Enumerable
            .Range(1, ProfileManifestLimits.MaximumCompositeGroupCount + 1)
            .Select(static number => $"group{number}")
            .ToArray();

        var result = _compiler.Compile(
            "literal",
            [CompositeField("value", groupNames, ProfileFieldValueKind.String)]);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "template_group_names_limit_exceeded");
    }

    [Theory]
    [InlineData("fields-null", "template_fields_missing")]
    [InlineData("fields-missing", "template_fields_missing")]
    [InlineData("template-missing", "template_missing")]
    [InlineData("placeholder-unclosed", "template_placeholder_unclosed")]
    [InlineData("literal-brace", "template_literal_brace_unescaped")]
    [InlineData("field-unknown", "template_field_unknown")]
    [InlineData("field-duplicate", "template_field_duplicate")]
    [InlineData("required-missing", "template_required_field_missing")]
    [InlineData("required-marked-optional", "template_optional_field_required")]
    [InlineData("optional-marked-required", "template_optional_field_not_optional")]
    [InlineData("optional-not-trailing", "template_optional_segment_not_trailing")]
    [InlineData("optional-multiple-placeholders", "template_optional_segment_multiple_placeholders")]
    public void Compile_reports_stable_diagnostics(
        string scenario,
        string expectedCode)
    {
        var (template, fields) = InvalidTemplateScenario(scenario);
        var result = _compiler.Compile(template, fields);

        Assert.False(result.IsValid);
        Assert.Null(result.Pattern);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Compile_rejects_invalid_field_metadata_without_throwing()
    {
        var fields = new[]
        {
            Field("Bad Id", "bad-group", (ProfileFieldValueKind)999),
            Field("name", "name", ProfileFieldValueKind.String),
            Field("name", "other", ProfileFieldValueKind.String),
            Field("bad-group", "bad-group", ProfileFieldValueKind.String, required: false),
            Field("bad-kind", "badKind", (ProfileFieldValueKind)999, required: false),
        };

        var result = _compiler.Compile("{name}", fields);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, static item => item.Code == "template_field_id_invalid");
        Assert.Contains(result.Diagnostics, static item => item.Code == "template_field_definition_duplicate");
        Assert.Contains(result.Diagnostics, static item => item.Code == "template_group_name_invalid");
        Assert.Contains(result.Diagnostics, static item => item.Code == "template_field_kind_invalid");
    }

    [Fact]
    public void Application_registration_resolves_one_compiler_instance()
    {
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IProfilePathTemplateCompiler>();
        var second = provider.GetRequiredService<IProfilePathTemplateCompiler>();

        Assert.IsType<ProfilePathTemplateCompiler>(first);
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Path_template_metadata_round_trips_without_changing_runtime_validation(
        bool includePathTemplate)
    {
        var pathTemplate = includePathTemplate ? "{name}" : null;
        var manifest = new ProfileManifest
        {
            ContractVersion = 1,
            Kind = ProfileKind.Declarative,
            Id = "template-compatibility",
            Version = "1.0.0",
            DisplayName = "Template compatibility",
            CandidateKind = ProfileCandidateKind.Directory,
            PathInput = ProfilePathInput.Relative,
            Fields =
            [
                Field("name", "name", ProfileFieldValueKind.String),
            ],
            Rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "default",
                    PathTemplate = pathTemplate,
                    Pattern = @"(?<name>[^\\/]+)",
                    MatchMode = ProfileRegexMatchMode.Full,
                    TimeoutMilliseconds = 100,
                },
            ],
        };
        var serializerOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var json = JsonSerializer.Serialize(manifest, serializerOptions);
        var restored = Assert.IsType<ProfileManifest>(
            JsonSerializer.Deserialize<ProfileManifest>(json, serializerOptions));
        var restoredRule = Assert.Single(restored.Rules!);

        Assert.Equal(pathTemplate, restoredRule.PathTemplate);
        if (includePathTemplate)
        {
            Assert.Contains("\"pathTemplate\"", json, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("\"pathTemplate\"", json, StringComparison.Ordinal);
        }

        var services = new ServiceCollection();
        services.AddProfileRuntime();
        using var provider = services.BuildServiceProvider();

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(restored);
        Assert.True(
            review.IsValid,
            string.Join(Environment.NewLine, review.Diagnostics.Select(static item => item.Message)));

        var mapping = review.Profile!.Map(new ProfilePathCandidate("Alpha", "Alpha"));
        Assert.Equal(ProfileMapStatus.Success, mapping.Status);
        Assert.Equal("Alpha", mapping.Item!.Values["name"]);
    }

    private static (string? Template, IReadOnlyList<ProfileFieldManifest>? Fields)
        InvalidTemplateScenario(string scenario) =>
        scenario switch
        {
            "fields-null" => ("literal", null),
            "fields-missing" => ("literal", Array.Empty<ProfileFieldManifest>()),
            "template-missing" => (null, RequiredNameFields()),
            "placeholder-unclosed" => ("Root/{name", RequiredNameFields()),
            "literal-brace" => ("Root/}", RequiredNameFields()),
            "field-unknown" => ("{missing}", RequiredNameFields()),
            "field-duplicate" => ("{name}/{name}", RequiredNameFields()),
            "required-missing" => ("Root", RequiredNameFields()),
            "required-marked-optional" => ("{name?}", RequiredNameFields()),
            "optional-marked-required" =>
                ("{name}/{note}",
                [
                    Field("name", "name", ProfileFieldValueKind.String),
                    Field("note", "note", ProfileFieldValueKind.String, required: false),
                ]),
            "optional-not-trailing" =>
                ("{note?}/{name}",
                [
                    Field("name", "name", ProfileFieldValueKind.String),
                    Field("note", "note", ProfileFieldValueKind.String, required: false),
                ]),
            "optional-multiple-placeholders" =>
                ("{name}/{note?}-{suffix?}",
                [
                    Field("name", "name", ProfileFieldValueKind.String),
                    Field("note", "note", ProfileFieldValueKind.String, required: false),
                    Field("suffix", "suffix", ProfileFieldValueKind.String, required: false),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

    private static IReadOnlyList<ProfileFieldManifest> RequiredNameFields() =>
        [Field("name", "name", ProfileFieldValueKind.String)];

    private static ProfileFieldManifest Field(
        string fieldId,
        string groupName,
        ProfileFieldValueKind kind,
        bool required = true) =>
        new()
        {
            FieldId = fieldId,
            GroupName = groupName,
            Header = fieldId,
            Required = required,
            Kind = kind,
        };

    private static ProfileFieldManifest CompositeField(
        string fieldId,
        IReadOnlyList<string> groupNames,
        ProfileFieldValueKind kind,
        bool required = true) =>
        new()
        {
            FieldId = fieldId,
            GroupNames = groupNames.ToList(),
            Header = fieldId,
            Required = required,
            Kind = kind,
            ParseFormat = kind == ProfileFieldValueKind.DateTime ? "yyyyMMdd" : null,
        };

    private static Regex FullMatch(string pattern) =>
        new(
            $@"\A(?:{pattern})\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
}
