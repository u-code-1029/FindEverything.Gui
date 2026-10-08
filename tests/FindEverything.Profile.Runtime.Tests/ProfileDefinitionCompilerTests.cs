using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
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

        var result = profile.Map(new ProfilePathCandidate(AbsolutePath(path)));

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
            AbsolutePath("2026/1332_Project")));

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
            @".*[\\/](?:(?<year>\d{4})[\\/](?<monthDay>\d{4})_)?(?<name>[^\\/]+)";
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate(AbsolutePath("Project")));

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
            @".*[\\/](?:(?<year>\d{4})(?:[\\/](?<monthDay>\d{4}))?_)?(?<name>[^\\/]+)";
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate(
            AbsolutePath("2026_Project")));

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
    public void Single_group_profile_maps_an_absolute_path()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Fields![0].GroupNames = null;
        manifest.Fields[0].GroupName = "date";
        manifest.Rules![0].Pattern = @".*[\\/](?<date>\d{8})_(?<name>[^\\/]+)";
        manifest.Rules[0].StopTraversalWhenCapturedGroups = null;
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate(
            AbsolutePath("20260521_Project")));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Equal(new DateTime(2026, 5, 21), result.Item!.Values["captured-on"]);
        Assert.False(result.ShouldPruneDescendants);
    }

    [Fact]
    public void Map_uses_only_the_absolute_candidate_path()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].Pattern =
            @".*[\\/]FULL[\\/](?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<name>[^\\/]+)";
        // Windows CI runs WPF and runtime suites in parallel. Keep this contract
        // test focused on absolute-path input instead of treating transient runner
        // CPU pressure as a catastrophic-regex timeout.
        manifest.Rules[0].TimeoutMilliseconds = 1_000;

        var profile = Compile(provider, manifest);
        var result = profile.Map(new ProfilePathCandidate(
            AbsolutePath("FULL/2026/0521_Project")));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Equal(new DateTime(2026, 5, 21), result.Item!.Values["captured-on"]);
        Assert.Equal("Project", result.Item.Values["name"]);
    }

    [Fact]
    public void Directory_exclusion_rules_match_only_the_supplied_leaf_name()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "generated-folder",
                Pattern = "name",
                MatchMode = ProfileRegexMatchMode.Full,
                TimeoutMilliseconds = 100,
            },
        ];
        var profile = Compile(provider, manifest);

        var excluded = profile.EvaluateDirectoryName("name");
        var similarName = profile.EvaluateDirectoryName("name-backup");
        var parentPathText = profile.EvaluateDirectoryName("C:-abc-def-name");

        Assert.True(excluded.IsExcluded);
        Assert.Equal("generated-folder", excluded.MatchedRuleId);
        Assert.False(similarName.IsExcluded);
        Assert.False(parentPathText.IsExcluded);
        var descriptor = Assert.Single(profile.Descriptor.ExcludedDirectoryNameRules);
        Assert.Equal("name", descriptor.Pattern);
        Assert.Equal(ProfileRegexMatchMode.Full, descriptor.MatchMode);
    }

    [Fact]
    public void Directory_exclusion_rule_can_use_partial_and_case_insensitive_matching()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "cache",
                Pattern = "cache",
                MatchMode = ProfileRegexMatchMode.Partial,
                IgnoreCase = true,
                TimeoutMilliseconds = 100,
            },
        ];
        var profile = Compile(provider, manifest);

        var result = profile.EvaluateDirectoryName("Project-CACHE-v2");

        Assert.True(result.IsExcluded);
        Assert.Equal("cache", result.MatchedRuleId);
    }

    [Fact]
    public void Directory_exclusion_regex_timeout_is_reported_and_fails_open()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "pathological",
                Pattern = "(a+)+$",
                MatchMode = ProfileRegexMatchMode.Full,
                TimeoutMilliseconds = 1,
            },
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "later-catch-all",
                Pattern = ".*",
                MatchMode = ProfileRegexMatchMode.Full,
                TimeoutMilliseconds = 100,
            },
        ];
        var profile = Compile(provider, manifest);

        var result = profile.EvaluateDirectoryName(new string('a', 100_000) + "!");

        Assert.False(result.IsExcluded);
        Assert.Null(result.MatchedRuleId);
        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "directory_exclusion_regex_timeout");
    }

    [Fact]
    public void Directory_exclusion_allows_a_whitespace_leaf_name()
    {
        using var provider = BuildProvider();
        var profile = Compile(provider, CompositeDateManifest());

        var exception = Record.Exception(() => profile.EvaluateDirectoryName("   "));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("(", "excluded_directory_name_regex_invalid", 100)]
    [InlineData("valid", "excluded_directory_name_timeout_invalid", 0)]
    [InlineData("valid", "excluded_directory_name_timeout_invalid", 10001)]
    public void Validation_rejects_invalid_directory_exclusion_regex_settings(
        string pattern,
        string expectedCode,
        int timeoutMilliseconds)
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "exclude",
                Pattern = pattern,
                TimeoutMilliseconds = timeoutMilliseconds,
            },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(review.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Validation_rejects_excessive_directory_exclusion_rules_and_pattern_length()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.ExcludedDirectoryNameRules = Enumerable
            .Range(1, ProfileManifestLimits.MaximumExcludedDirectoryNameRuleCount + 1)
            .Select(index => new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = $"exclude-{index}",
                Pattern = index == 1
                    ? new string(
                        'x',
                        ProfileManifestLimits.MaximumExcludedDirectoryNamePatternLength + 1)
                    : $"folder-{index}",
                TimeoutMilliseconds = 100,
            })
            .ToList();

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "excluded_directory_name_rule_limit_exceeded");
        Assert.Contains(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "excluded_directory_name_pattern_too_long");
    }

    [Fact]
    public void Validation_rejects_excessive_path_rule_count()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules = Enumerable
            .Range(1, ProfileManifestLimits.MaximumRegexRuleCount + 1)
            .Select(index => new ProfileRegexRuleManifest
            {
                Id = $"rule-{index}",
                Pattern = "never-match",
                MatchMode = ProfileRegexMatchMode.Partial,
                TimeoutMilliseconds = 100,
            })
            .ToList();

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "regex_rule_limit_exceeded");
    }

    [Fact]
    public void Validation_rejects_path_rule_aggregate_timeout_budget()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        var timeout = (ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds / 2) + 1;
        manifest.Rules =
        [
            new ProfileRegexRuleManifest
            {
                Id = "first",
                Pattern = "first",
                MatchMode = ProfileRegexMatchMode.Partial,
                TimeoutMilliseconds = timeout,
            },
            new ProfileRegexRuleManifest
            {
                Id = "second",
                Pattern = "second",
                MatchMode = ProfileRegexMatchMode.Partial,
                TimeoutMilliseconds = timeout,
            },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        var diagnostic = Assert.Single(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "regex_timeout_budget_exceeded");
        Assert.Contains("현재 합계", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_accepts_path_rule_timeout_at_the_aggregate_budget_boundary()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].TimeoutMilliseconds =
            ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds;

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.True(review.IsValid);
    }

    [Fact]
    public void Validation_rejects_combined_path_and_exclusion_timeout_budget()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        var pathTimeout = ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds / 2;
        var exclusionTimeout =
            ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds - pathTimeout + 1;
        manifest.Rules![0].TimeoutMilliseconds = pathTimeout;
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "slow-exclusion",
                Pattern = "slow",
                MatchMode = ProfileRegexMatchMode.Partial,
                TimeoutMilliseconds = exclusionTimeout,
            },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        var diagnostic = Assert.Single(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "regex_timeout_budget_exceeded");
        Assert.Contains(
            $"경로: {pathTimeout:N0}ms",
            diagnostic.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"폴더 이름 제외: {exclusionTimeout:N0}ms",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_accepts_combined_timeout_at_the_aggregate_budget_boundary()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        const int pathTimeout = 100;
        manifest.Rules![0].TimeoutMilliseconds = pathTimeout;
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "bounded-exclusion",
                Pattern = "bounded",
                MatchMode = ProfileRegexMatchMode.Partial,
                TimeoutMilliseconds =
                    ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds - pathTimeout,
            },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.True(review.IsValid);
    }

    [Fact]
    public void Validation_rejects_exclusion_rules_that_exhaust_the_shared_timeout_budget()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].TimeoutMilliseconds = 1;
        manifest.ExcludedDirectoryNameRules =
        [
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "first-exclusion",
                Pattern = "first",
                TimeoutMilliseconds = 5_000,
            },
            new ProfileDirectoryNameExclusionRuleManifest
            {
                Id = "second-exclusion",
                Pattern = "second",
                TimeoutMilliseconds = 5_000,
            },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        var diagnostic = Assert.Single(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "regex_timeout_budget_exceeded");
        Assert.Contains("폴더 이름 제외: 10,000ms", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("현재 합계: 10,001ms", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_rejects_excessive_path_pattern_length()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].Pattern = new string(
            'x',
            ProfileManifestLimits.MaximumRegexPatternLength + 1);

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            static diagnostic => diagnostic.Code == "regex_pattern_too_long");
    }

    [Fact]
    public void Test_rejects_a_relative_sample_path()
    {
        using var provider = BuildProvider();
        var compiler = provider.GetRequiredService<IProfileDefinitionCompiler>();

        var exception = Assert.Throws<ArgumentException>(() =>
            compiler.Test(CompositeDateManifest(), Path.Combine("2026", "0521_Project")));

        Assert.Equal("samplePath", exception.ParamName);
        Assert.Contains("전체 폴더 경로", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_path_candidate_rejects_relative_input()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ProfilePathCandidate(Path.Combine("2026", "0521_Project")));

        Assert.Equal("absolutePath", exception.ParamName);
        Assert.Contains("절대 경로", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_normalizes_a_fully_qualified_sample_and_maps_the_full_path()
    {
        using var provider = BuildProvider();
        var compiler = provider.GetRequiredService<IProfileDefinitionCompiler>();
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FindEverything.Profile.Runtime.Tests",
            "full-path-sample"));
        var samplePath = Path.Combine(root, ".", "2026", "0521_Project");
        var normalizedPath = Path.GetFullPath(samplePath);
        var manifest = CompositeDateManifest();
        manifest.Rules![0].Pattern = string.Concat(
            "^",
            Regex.Escape(root + Path.DirectorySeparatorChar),
            @"(?<year>\d{4})",
            Regex.Escape(Path.DirectorySeparatorChar.ToString()),
            @"(?<monthDay>\d{4})_(?<name>[^\\/]+)$");

        var test = compiler.Test(manifest, samplePath);

        Assert.True(test.Review.IsValid);
        Assert.Equal(ProfileMapStatus.Success, test.Mapping!.Status);
        Assert.Equal(normalizedPath, test.Mapping.Item!.FullPath);
        Assert.Equal("Project", test.Mapping.Item.Values["name"]);
    }

    [Fact]
    public void Test_maps_the_path_returned_by_the_registered_canonicalizer()
    {
        var suppliedPath = AbsolutePath("mapped", "2026", "0521_Project");
        var canonicalPath = AbsolutePath("unc", "192.168.10.20", "share", "2026", "0521_Project");
        var services = new ServiceCollection();
        services.AddProfileRuntime();
        services.AddSingleton<IProfilePathCanonicalizer>(
            new StubPathCanonicalizer(canonicalPath));
        using var provider = services.BuildServiceProvider();
        var manifest = CompositeDateManifest();
        manifest.Rules![0].Pattern = Regex.Escape(
                Path.GetDirectoryName(Path.GetDirectoryName(canonicalPath)!)!
                + Path.DirectorySeparatorChar)
            + @"(?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<name>[^\\/]+)";

        var test = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Test(manifest, suppliedPath);

        Assert.Equal(ProfileMapStatus.Success, test.Mapping?.Status);
        Assert.Equal(canonicalPath, test.Mapping?.Item?.FullPath);
        Assert.Equal(canonicalPath, Assert.IsType<StubPathCanonicalizer>(
            provider.GetServices<IProfilePathCanonicalizer>().Last()).Result);
    }

    [Fact]
    public void Terminal_groups_must_all_have_non_whitespace_captures()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest(required: false);
        manifest.Rules![0].Pattern =
            @".*[\\/](?:(?<year>\d{4})[\\/](?<monthDay>\d{4})_)?(?<name>[^\\/_]+)(?:_(?<leaf>\S+))?";
        manifest.Rules[0].StopTraversalWhenCapturedGroups = ["name", "leaf"];
        var profile = Compile(provider, manifest);

        var branch = profile.Map(new ProfilePathCandidate(AbsolutePath("Project")));
        var leaf = profile.Map(new ProfilePathCandidate(AbsolutePath("Project_done")));

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
            @".*[\\/](?<year>(a+)+)$(?:(?<monthDay>\d{4})_(?<name>[^\\/]+))?";
        manifest.Rules[0].TimeoutMilliseconds = 1;
        var profile = Compile(provider, manifest);
        var path = AbsolutePath(new string('a', 10_000) + "!");

        var result = profile.Map(new ProfilePathCandidate(path));

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

    [Fact]
    public void Value_mapping_keeps_the_raw_model_and_exposes_a_mapped_display_value()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        var nameField = manifest.Fields![1];
        nameField.GroupName = "alias";
        nameField.ValueMappings =
        [
            new ProfileValueMappingManifest { Source = " A", Display = " 홍길동 " },
        ];
        manifest.Rules![0].Pattern =
            @".*[\\/](?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<alias>[^\\/]+)";
        var profile = Compile(provider, manifest);

        var result = profile.Map(new ProfilePathCandidate(
            AbsolutePath("2026/0521_ A")));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Equal(" A", result.Item!.Values["name"]);
        Assert.Equal(" 홍길동 ", result.Item.DisplayValues["name"]);
        Assert.Equal(
            " A",
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Item.Model)["name"]);
        var descriptor = Assert.Single(
            profile.Descriptor.Fields,
            static field => field.FieldId == "name");
        Assert.Equal(" 홍길동 ", descriptor.ValueMappings[" A"]);
    }

    [Theory]
    [InlineData(ProfileFieldValueKind.Int32, "capture_value_mapping_kind_unsupported")]
    [InlineData(ProfileFieldValueKind.String, "capture_value_mapping_source_duplicate")]
    public void Validation_rejects_invalid_value_mapping_contracts(
        ProfileFieldValueKind kind,
        string expectedCode)
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        var nameField = manifest.Fields![1];
        nameField.Kind = kind;
        nameField.ValueMappings =
        [
            new ProfileValueMappingManifest { Source = "A", Display = "First" },
            new ProfileValueMappingManifest { Source = "A", Display = "Second" },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(review.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Validation_reports_a_null_value_mapping_entry_instead_of_throwing()
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Fields![1].ValueMappings = [null!];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(
            review.Diagnostics,
            diagnostic => diagnostic.Code == "capture_value_mapping_missing");
    }

    [Theory]
    [InlineData("   ", "Display", "capture_value_mapping_source_missing")]
    [InlineData("A", "   ", "capture_value_mapping_display_missing")]
    public void Validation_rejects_whitespace_only_value_mapping_parts(
        string source,
        string display,
        string expectedCode)
    {
        using var provider = BuildProvider();
        var manifest = CompositeDateManifest();
        manifest.Fields![1].ValueMappings =
        [
            new ProfileValueMappingManifest { Source = source, Display = display },
        ];

        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(manifest);

        Assert.False(review.IsValid);
        Assert.Contains(review.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
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
            ContractVersion = ProfileContract.CurrentMajor,
            Kind = ProfileKind.Declarative,
            Id = "composite-date",
            Version = "1.0.0",
            DisplayName = "Composite date",
            CandidateKind = ProfileCandidateKind.Directory,
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
                    Pattern = @".*[\\/](?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<name>[^\\/]+)",
                    MatchMode = ProfileRegexMatchMode.Full,
                    TimeoutMilliseconds = 100,
                    StopTraversalWhenCapturedGroups = ["year", "monthDay"],
                },
            ],
        };

    private static string AbsolutePath(params string[] relativeSegments)
    {
        var segments = relativeSegments
            .SelectMany(static segment => segment.Split(
                ['\\', '/'],
                StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        return Path.GetFullPath(Path.Combine(
            [Path.GetTempPath(), "FindEverything.Profile.Runtime.Tests", .. segments]));
    }

    private sealed class StubPathCanonicalizer(string result) : IProfilePathCanonicalizer
    {
        public string Result { get; } = result;

        public string Canonicalize(string path) => Result;
    }
}
