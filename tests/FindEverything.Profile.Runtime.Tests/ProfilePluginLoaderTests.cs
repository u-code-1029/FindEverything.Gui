using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using FindEverything.Profile.Abstractions;
using FindEverything.Profiles.SampleProjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfilePluginLoaderTests
{
    private static readonly string ScanRoot = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "FindEverything.Profile.Runtime.Tests",
        "scan-root"));

    private static readonly string ValidPattern = ScanPattern(
        @"(?<name>[^\\/]+)[\\/](?<year>\d{4})(?:[\\/]r(?<revision>\d+))?(?:[\\/](?<capturedOn>\d{8}))?(?:[\\/](?<approved>true|false))?(?:[\\/](?<amount>-?\d+(?:\.\d+)?))?");

    [Fact]
    public async Task Bundled_sample_profile_maps_the_same_suffix_under_different_full_roots()
    {
        using var packages = new TestProfilePackages();
        var packageDirectory = Path.Combine(packages.RootPath, "sample-projects");
        Directory.CreateDirectory(packageDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-projects", "profile.json"),
            Path.Combine(packageDirectory, "profile.json"));
        CopyAssemblyPackage(typeof(SampleProjectItem).Assembly.Location, packageDirectory);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        var profile = Assert.Single(snapshot.Profiles);
        Assert.Equal("sample-projects", profile.Descriptor.Id);
        var suffix = Path.Combine(
            "Clients",
            "Acme",
            "Projects",
            "Project-X",
            "2026",
            "20260521",
            "Rev-7",
            "Approved-true",
            "Amount-1234.50");
        var roots = new[]
        {
            Path.Combine(Path.GetTempPath(), "sample-root-a"),
            Path.Combine(Path.GetTempPath(), "different", "nested", "sample-root-b"),
        };

        foreach (var root in roots)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, suffix));
            var mapping = profile.Map(new ProfilePathCandidate(fullPath));

            Assert.Equal(ProfileMapStatus.Success, mapping.Status);
            Assert.Equal(fullPath, mapping.Item!.FullPath);
            Assert.Equal("Acme", mapping.Item.Values["customer"]);
            Assert.Equal("Project-X", mapping.Item.Values["project"]);
            Assert.Equal(2026, mapping.Item.Values["year"]);
            Assert.Equal(new DateTime(2026, 5, 21), mapping.Item.Values["captured-on"]);
            Assert.Equal(7, mapping.Item.Values["revision"]);
            Assert.Equal(true, mapping.Item.Values["approved"]);
            Assert.Equal(1234.50m, mapping.Item.Values["amount"]);
        }
    }

    [Fact]
    public async Task LoadAsync_MapsAllSupportedTypesAndNullableValues()
    {
        using var packages = new TestProfilePackages();
        packages.Add("valid", "valid-profile", ValidPattern);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        var profile = Assert.Single(snapshot.Profiles);
        Assert.Equal("valid-profile", profile.Descriptor.Id);
        Assert.Equal(ProfileKind.Assembly, profile.Descriptor.Kind);
        Assert.Equal(
            new[] { "name", "year", "revision", "captured-on", "approved", "amount" },
            profile.Descriptor.Fields.Select(static field => field.FieldId));

        var complete = profile.Map(new ProfilePathCandidate(
            ScanPath("alpha", "2026", "r7", "20261007", "true", "1234.50")));

        Assert.Equal(ProfileMapStatus.Success, complete.Status);
        Assert.NotNull(complete.Item);
        Assert.Equal("alpha", complete.Item.Values["name"]);
        Assert.Equal(2026, complete.Item.Values["year"]);
        Assert.Equal(7, complete.Item.Values["revision"]);
        Assert.Equal(new DateTime(2026, 10, 7), complete.Item.Values["captured-on"]);
        Assert.Equal(true, complete.Item.Values["approved"]);
        Assert.Equal(1234.50m, complete.Item.Values["amount"]);

        var minimal = profile.Map(new ProfilePathCandidate(
            ScanPath("beta", "2025")));

        Assert.Equal(ProfileMapStatus.Success, minimal.Status);
        Assert.NotNull(minimal.Item);
        Assert.Null(minimal.Item.Values["revision"]);
        Assert.Null(minimal.Item.Values["captured-on"]);
        Assert.Null(minimal.Item.Values["approved"]);
        Assert.Null(minimal.Item.Values["amount"]);
    }

    [Fact]
    public async Task LoadAsync_AssemblyProfileConcatenatesOrderedCaptureGroups()
    {
        using var packages = new TestProfilePackages();
        packages.Add(
            "composite-date",
            "composite-date-profile",
            ScanPattern(@"(?<year>\d{4})[\\/](?<monthDay>\d{4})"),
            modelType: typeof(CompositeCaptureProfileModel));
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        var profile = Assert.Single(snapshot.Profiles);
        var field = Assert.Single(profile.Descriptor.Fields);
        Assert.Equal(["year", "monthDay"], field.EffectiveGroupNames);

        var result = profile.Map(new ProfilePathCandidate(
            ScanPath("2026", "0521")));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.Equal(new DateTime(2026, 5, 21), result.Item!.Values["captured-on"]);
    }

    [Fact]
    public async Task LoadAsync_DeclarativeProfileMapsValuesWithoutAnAssembly()
    {
        using var packages = new TestProfilePackages();
        packages.AddDeclarative("declarative", "declarative-profile", ValidPattern);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        var profile = Assert.Single(snapshot.Profiles);
        Assert.Equal(ProfileKind.Declarative, profile.Descriptor.Kind);
        Assert.Equal(
            new[] { "name", "year", "revision", "captured-on", "approved", "amount" },
            profile.Descriptor.Fields.Select(static field => field.FieldId));

        var result = profile.Map(new ProfilePathCandidate(
            ScanPath("alpha", "2026", "r7", "20261007", "true", "1234.50")));

        Assert.Equal(ProfileMapStatus.Success, result.Status);
        Assert.NotNull(result.Item);
        Assert.Equal("alpha", result.Item.Values["name"]);
        Assert.Equal(2026, result.Item.Values["year"]);
        Assert.Equal(7, result.Item.Values["revision"]);
        Assert.Equal(new DateTime(2026, 10, 7), result.Item.Values["captured-on"]);
        Assert.Equal(true, result.Item.Values["approved"]);
        Assert.Equal(1234.50m, result.Item.Values["amount"]);
        Assert.Same(result.Item.Values, result.Item.Model);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Item.Model);

        var minimal = profile.Map(new ProfilePathCandidate(
            ScanPath("beta", "2025")));

        Assert.Equal(ProfileMapStatus.Success, minimal.Status);
        Assert.NotNull(minimal.Item);
        Assert.Null(minimal.Item.Values["revision"]);
        Assert.Null(minimal.Item.Values["captured-on"]);
        Assert.Null(minimal.Item.Values["approved"]);
        Assert.Null(minimal.Item.Values["amount"]);
    }

    [Fact]
    public async Task LoadAsync_DeclarativeProfileReadsDeterministicImmediateTextFile()
    {
        using var packages = new TestProfilePackages();
        var candidateDirectory = Path.Combine(Path.GetTempPath(), $"findeverything-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidateDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(candidateDirectory, "INFO-b.txt"),
                "second",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(
                Path.Combine(candidateDirectory, "info-A.txt"),
                "first 한글",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var nestedDirectory = Directory.CreateDirectory(
                Path.Combine(candidateDirectory, "nested"));
            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "info-0.txt"), "nested");

            packages.AddDeclarative(
                "text-file",
                "text-file-profile",
                Regex.Escape(candidateDirectory),
                includeFields: false,
                textFileFields:
                [
                    CreateTextFileField(
                        "description",
                        @"^info-.*\.txt$",
                        required: true),
                ]);
            using var host = BuildHost(packages.RootPath);

            var profile = Assert.Single((await LoadAsync(host)).Profiles);
            var descriptor = Assert.Single(profile.Descriptor.Fields);
            Assert.Equal(ProfileFieldSourceKind.TextFileContent, descriptor.SourceKind);
            Assert.Empty(descriptor.EffectiveGroupNames);
            Assert.Single(profile.Descriptor.TextFileFields);

            var result = profile.Map(new ProfilePathCandidate(candidateDirectory));

            Assert.Equal(ProfileMapStatus.Success, result.Status);
            Assert.Equal("first 한글", result.Item!.Values["description"]);
            Assert.Same(result.Item.Values, result.Item.Model);
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_TextFileFieldHonorsOptionalRequiredAndMaximumBytes()
    {
        using var packages = new TestProfilePackages();
        var candidateDirectory = Path.Combine(Path.GetTempPath(), $"findeverything-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidateDirectory);
        try
        {
            File.WriteAllText(Path.Combine(candidateDirectory, "large.txt"), "12345");
            packages.AddDeclarative(
                "text-file",
                "text-file-profile",
                Regex.Escape(candidateDirectory),
                includeFields: false,
                textFileFields:
                [
                    CreateTextFileField("optional", @"^missing\.txt$", required: false),
                    CreateTextFileField("required", @"^required\.txt$", required: true),
                    CreateTextFileField("large", @"^large\.txt$", required: false, maxBytes: 4),
                ]);
            using var host = BuildHost(packages.RootPath);

            var profile = Assert.Single((await LoadAsync(host)).Profiles);
            var result = profile.Map(new ProfilePathCandidate(candidateDirectory));

            Assert.Equal(ProfileMapStatus.Invalid, result.Status);
            Assert.Contains(result.Issues, static issue =>
                issue.Code == "required_text_file_missing" && issue.FieldId == "required");
            Assert.Contains(result.Issues, static issue =>
                issue.Code == "text_file_too_large" && issue.FieldId == "large");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_LoadsBundledAndUserProfileRootsTogether()
    {
        using var bundledPackages = new TestProfilePackages();
        bundledPackages.Add("assembly", "assembly-profile", ValidPattern);
        using var userPackages = new TestProfilePackages();
        userPackages.AddDeclarative("declarative", "user-profile", ValidPattern);
        using var host = BuildHost(bundledPackages.RootPath, userPackages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Equal(2, snapshot.Profiles.Count);
        Assert.Contains(snapshot.Profiles, static profile =>
            profile.Descriptor.Id == "assembly-profile"
            && profile.Descriptor.Kind == ProfileKind.Assembly);
        Assert.Contains(snapshot.Profiles, static profile =>
            profile.Descriptor.Id == "user-profile"
            && profile.Descriptor.Kind == ProfileKind.Declarative);
    }

    [Fact]
    public async Task LoadAsync_DisablesDeclarativeProfileWithoutFields()
    {
        using var packages = new TestProfilePackages();
        packages.AddDeclarative(
            "missing-fields",
            "missing-fields-profile",
            ValidPattern,
            includeFields: false);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var report = Assert.Single(snapshot.Reports);
        Assert.Equal(ProfilePluginStatus.Disabled, report.Status);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == "capture_fields_missing");
    }

    [Fact]
    public async Task LoadAsync_DisablesDeclarativeProfileWithInvalidFieldContract()
    {
        using var packages = new TestProfilePackages();
        packages.AddDeclarative(
            "invalid-fields",
            "invalid-fields-profile",
            ValidPattern,
            fields: new object[]
            {
                CreateDeclarativeField("duplicate", "valid", "String", required: true),
                CreateDeclarativeField("duplicate", "not-valid-group", "Int32", required: false),
            });
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var diagnostics = Assert.Single(snapshot.Reports).Diagnostics;
        Assert.Contains(diagnostics, static item => item.Code == "capture_field_id_duplicate");
        Assert.Contains(diagnostics, static item => item.Code == "capture_group_name_invalid");
    }

    [Fact]
    public async Task LoadAsync_DisablesTextFileFieldWithDuplicateIdAndUnsafeReadLimit()
    {
        using var packages = new TestProfilePackages();
        packages.AddDeclarative(
            "invalid-text-field",
            "invalid-text-field-profile",
            ValidPattern,
            textFileFields:
            [
                CreateTextFileField(
                    "name",
                    @"^info\.txt$",
                    required: false,
                    maxBytes: ProfileManifestLimits.MaximumTextFileMaximumBytes + 1),
            ]);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var diagnostics = Assert.Single(snapshot.Reports).Diagnostics;
        Assert.Contains(diagnostics, static item =>
            item.Code == "text_file_field_id_duplicate");
        Assert.Contains(diagnostics, static item =>
            item.Code == "text_file_max_bytes_invalid");
    }

    [Fact]
    public async Task LoadAsync_DisablesDeclarativeProfileWithInvalidValueFormats()
    {
        using var packages = new TestProfilePackages();
        packages.AddDeclarative(
            "invalid-formats",
            "invalid-formats-profile",
            "^(?<capturedOn>.+)/(?<label>.+)$",
            fields: new object[]
            {
                CreateDeclarativeField(
                    "captured-on",
                    "capturedOn",
                    "DateTime",
                    required: true,
                    parseFormat: "%"),
                CreateDeclarativeField(
                    "label",
                    "label",
                    "String",
                    required: true,
                    displayFormat: "N2"),
            });
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var diagnostics = Assert.Single(snapshot.Reports).Diagnostics;
        Assert.Contains(diagnostics, static item => item.Code == "capture_parse_format_invalid");
        Assert.Contains(diagnostics, static item => item.Code == "capture_display_format_invalid");
    }

    [Fact]
    public async Task Map_FirstMatchingRuleOwnsPathWhenConversionFails()
    {
        using var packages = new TestProfilePackages();
        packages.Add(
            "priority",
            "priority-profile",
            ScanPattern(@"(?<name>[^\\/]+)[\\/](?<year>Y\d+)"),
            ScanPattern(@"(?<name>[^\\/]+)[\\/]Y(?<year>\d+)"));
        using var host = BuildHost(packages.RootPath);
        var snapshot = await LoadAsync(host);
        var profile = Assert.Single(snapshot.Profiles);

        var result = profile.Map(new ProfilePathCandidate(
            ScanPath("alpha", "Y2026")));

        Assert.Equal(ProfileMapStatus.Invalid, result.Status);
        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "capture_conversion_failed" && issue.FieldId == "year");
    }

    [Fact]
    public async Task LoadAsync_DisablesEveryPluginWithADuplicateId()
    {
        using var packages = new TestProfilePackages();
        packages.Add("duplicate-a", "duplicate", ValidPattern);
        packages.Add("duplicate-b", "duplicate", ValidPattern);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var reports = snapshot.Reports
            .Where(static report => report.ProfileId == "duplicate")
            .ToArray();
        Assert.Equal(2, reports.Length);
        Assert.All(reports, static report =>
        {
            Assert.Equal(ProfilePluginStatus.Disabled, report.Status);
            Assert.Contains(report.Diagnostics, static item => item.Code == "profile_id_duplicate");
        });
    }

    [Fact]
    public async Task LoadAsync_IsolatesInvalidPluginAndLoadsValidPlugin()
    {
        using var packages = new TestProfilePackages();
        packages.Add("valid", "valid-profile", ValidPattern);
        packages.Add(
            "future",
            "future-profile",
            ValidPattern,
            contractVersion: ProfileContract.CurrentMajor + 1);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Equal("valid-profile", Assert.Single(snapshot.Profiles).Descriptor.Id);
        var invalidReport = Assert.Single(snapshot.Reports, static report =>
            report.ProfileId == "future-profile");
        Assert.Equal(ProfilePluginStatus.Disabled, invalidReport.Status);
        Assert.Contains(invalidReport.Diagnostics, static item => item.Code == "contract_unsupported");
    }

    [Fact]
    public async Task LoadAsync_RejectsTheLegacyRelativePathContract()
    {
        using var packages = new TestProfilePackages();
        packages.Add(
            "legacy",
            "legacy-profile",
            ValidPattern,
            contractVersion: ProfileContract.CurrentMajor - 1);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var report = Assert.Single(snapshot.Reports);
        Assert.Equal(ProfilePluginStatus.Disabled, report.Status);
        Assert.Contains(report.Diagnostics, static item => item.Code == "contract_unsupported");
    }

    [Fact]
    public async Task LoadAsync_DisablesValidAndInvalidManifestsThatShareAnId()
    {
        using var packages = new TestProfilePackages();
        packages.Add("valid", "same-id", ValidPattern);
        packages.Add(
            "future",
            "same-id",
            ValidPattern,
            contractVersion: ProfileContract.CurrentMajor + 1);
        using var host = BuildHost(packages.RootPath);

        var snapshot = await LoadAsync(host);

        Assert.Empty(snapshot.Profiles);
        var reports = snapshot.Reports.Where(static report => report.ProfileId == "same-id").ToArray();
        Assert.Equal(2, reports.Length);
        Assert.All(reports, static report =>
            Assert.Contains(report.Diagnostics, static item => item.Code == "profile_id_duplicate"));
        Assert.Contains(
            reports.SelectMany(static report => report.Diagnostics),
            static item => item.Code == "contract_unsupported");
    }

    [Fact]
    public async Task Map_ReturnsInvalidWhenRegexTimesOut()
    {
        using var packages = new TestProfilePackages();
        packages.Add(
            "timeout",
            "timeout-profile",
            ScanPattern(@"(?<name>(a+)+)(?<year>\d+)"),
            timeoutMilliseconds: 1);
        using var host = BuildHost(packages.RootPath);
        var snapshot = await LoadAsync(host);
        var profile = Assert.Single(snapshot.Profiles);

        var input = new string('a', 10_000) + "!";
        var result = profile.Map(new ProfilePathCandidate(
            ScanPath(input)));

        Assert.Equal(ProfileMapStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, static issue => issue.Code == "regex_timeout");
    }

    private static IHost BuildHost(
        string profilesDirectory,
        string? userProfilesDirectory = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddProfileRuntime();
        builder.Services.Configure<PluginDiscoveryOptions>(options =>
        {
            options.ProfilesDirectory = profilesDirectory;
            options.UserProfilesDirectory = userProfilesDirectory;
            options.ContractMajor = ProfileContract.CurrentMajor;
        });
        return builder.Build();
    }

    private static Task<ProfileCatalogSnapshot> LoadAsync(IHost host) =>
        host.Services.GetRequiredService<IProfilePluginLoader>().LoadAsync();

    private static string ScanPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine([ScanRoot, .. segments]));

    private static string ScanPattern(string suffixPattern) =>
        $"^{Regex.Escape(ScanRoot)}[\\\\/]{suffixPattern}$";

    private static void CopyAssemblyPackage(string assemblyPath, string destinationDirectory)
    {
        File.Copy(
            assemblyPath,
            Path.Combine(destinationDirectory, Path.GetFileName(assemblyPath)));
        var dependencyFile = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (File.Exists(dependencyFile))
        {
            File.Copy(
                dependencyFile,
                Path.Combine(destinationDirectory, Path.GetFileName(dependencyFile)));
        }
    }

    private sealed class TestProfilePackages : IDisposable
    {
        private readonly string _assemblyPath = typeof(TestProfileModel).Assembly.Location;

        public TestProfilePackages()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "FindEverything.Profile.Runtime.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Add(
            string directoryName,
            string profileId,
            string firstPattern,
            string? secondPattern = null,
            int contractVersion = ProfileContract.CurrentMajor,
            int timeoutMilliseconds = 100,
            Type? modelType = null)
        {
            var directory = Path.Combine(RootPath, directoryName);
            Directory.CreateDirectory(directory);
            var assemblyFileName = Path.GetFileName(_assemblyPath);
            File.Copy(_assemblyPath, Path.Combine(directory, assemblyFileName));

            var sourceDependencyFile = Path.ChangeExtension(_assemblyPath, ".deps.json");
            if (File.Exists(sourceDependencyFile))
            {
                File.Copy(
                    sourceDependencyFile,
                    Path.Combine(directory, Path.GetFileName(sourceDependencyFile)));
            }

            var rules = new List<object>
            {
                CreateRule("first", firstPattern, timeoutMilliseconds),
            };
            if (secondPattern is not null)
            {
                rules.Add(CreateRule("second", secondPattern, timeoutMilliseconds));
            }

            modelType ??= typeof(TestProfileModel);
            var manifest = new
            {
                contractVersion,
                id = profileId,
                version = "1.0.0",
                displayName = profileId,
                entryAssembly = assemblyFileName,
                modelType = modelType.FullName,
                candidateKind = "Directory",
                rules,
            };

            File.WriteAllText(
                Path.Combine(directory, "profile.json"),
                JsonSerializer.Serialize(manifest));
        }

        public void AddDeclarative(
            string directoryName,
            string profileId,
            string pattern,
            bool includeFields = true,
            IReadOnlyList<object>? fields = null,
            IReadOnlyList<object>? textFileFields = null)
        {
            var directory = Path.Combine(RootPath, directoryName);
            Directory.CreateDirectory(directory);

            var manifest = new
            {
                contractVersion = ProfileContract.CurrentMajor,
                kind = "Declarative",
                id = profileId,
                version = "1.0.0",
                displayName = profileId,
                candidateKind = "Directory",
                fields = includeFields
                    ? fields ?? CreateDeclarativeFields()
                    : null,
                textFileFields,
                rules = new[]
                {
                    CreateRule("first", pattern, 100),
                },
            };

            File.WriteAllText(
                Path.Combine(directory, "profile.json"),
                JsonSerializer.Serialize(manifest));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                // A test failure should not be hidden by best-effort fixture cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // A test failure should not be hidden by best-effort fixture cleanup.
            }
        }

        private static object CreateRule(string id, string pattern, int timeoutMilliseconds) =>
            new
            {
                id,
                pattern,
                matchMode = "Full",
                ignoreCase = false,
                timeoutMilliseconds,
            };

        private static IReadOnlyList<object> CreateDeclarativeFields() =>
            new object[]
            {
                CreateDeclarativeField("name", "name", "String", required: true, order: 10),
                CreateDeclarativeField("year", "year", "Int32", required: true, order: 20),
                CreateDeclarativeField("revision", "revision", "Int32", required: false, order: 30),
                CreateDeclarativeField(
                    "captured-on",
                    "capturedOn",
                    "DateTime",
                    required: false,
                    order: 40,
                    parseFormat: "yyyyMMdd",
                    displayFormat: "yyyy-MM-dd"),
                CreateDeclarativeField("approved", "approved", "Boolean", required: false, order: 50),
                CreateDeclarativeField(
                    "amount",
                    "amount",
                    "Decimal",
                    required: false,
                    order: 60,
                    displayFormat: "N2"),
            };
    }

    private static object CreateDeclarativeField(
        string fieldId,
        string groupName,
        string kind,
        bool required,
        int order = 0,
        string? parseFormat = null,
        string? displayFormat = null) =>
        new
        {
            fieldId,
            groupName,
            header = fieldId,
            order,
            required,
            kind,
            parseFormat,
            displayFormat,
        };

    private static object CreateTextFileField(
        string fieldId,
        string fileNamePattern,
        bool required,
        long maxBytes = ProfileManifestLimits.DefaultTextFileMaximumBytes) =>
        new
        {
            fieldId,
            header = fieldId,
            order = 100,
            required,
            fileNamePattern,
            matchMode = "Full",
            ignoreCase = true,
            timeoutMilliseconds = 100,
            maxBytes,
        };
}
