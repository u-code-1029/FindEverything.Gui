using System.Text;
using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfileResourceFieldTests
{
    [Fact]
    public void V2_definitions_and_explicit_v2_host_settings_remain_loadable()
    {
        var services = new ServiceCollection(); services.AddProfileRuntime();
        services.Configure<PluginDiscoveryOptions>(options => options.ContractMajor = 2);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(3, provider.GetRequiredService<IOptions<PluginDiscoveryOptions>>().Value.ContractMajor);
        var manifest = Definition(); manifest.ContractVersion = 2;
        Assert.True(provider.GetRequiredService<IProfileDefinitionCompiler>().Validate(manifest).IsValid);
    }

    [Theory]
    [InlineData("folder")][InlineData("depth")][InlineData("selection")][InlineData("edit")]
    public void New_features_require_v3_instead_of_being_silently_ignored_by_old_hosts(string feature)
    {
        var manifest = Definition(); manifest.ContractVersion = 2;
        if (feature == "folder") manifest.ChildDirectoryFields = [Field("ready", "Ready")];
        if (feature == "depth") manifest.MaxScanDepth = 1;
        if (feature == "selection") manifest.SelectionMatchRules = [new() { Id = "select", DisplayName = "Select", Columns = [new() { FieldId = "path" }] }];
        if (feature == "edit") manifest.TextFileFields = [new() { FieldId = "notes", FileNamePattern = "notes\\.txt", EditOptions = new() { Enabled = true } }];
        Assert.Contains(Review(manifest).Diagnostics, diagnostic => diagnostic.Code == "profile_feature_requires_v3");
    }

    [Fact]
    public void Creation_templates_allow_field_tokens_but_reject_paths()
    {
        var manifest = Definition(); manifest.TextFileFields = [new() { FieldId = "notes", FileNamePattern = ".*\\.txt", EditOptions = new() { Enabled = true, CreationFileNameTemplate = "{Field:path}.txt" } }];
        Assert.True(Review(manifest).IsValid);
        manifest.TextFileFields[0].EditOptions!.CreationFileNameTemplate = "C:escape.txt";
        Assert.False(Review(manifest).IsValid);
    }

    [Fact]
    public void Child_presence_is_literal_immediate_and_boolean_with_configurable_case()
    {
        using var folder = new Folder(); Directory.CreateDirectory(Path.Combine(folder.Path, "Complete"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "nested", "DeepOnly"));
        File.WriteAllText(Path.Combine(folder.Path, "NotAFolder"), "text");
        var manifest = Definition();
        manifest.ChildDirectoryFields =
        [ Field("complete", "complete"), Field("deep", "DeepOnly"), Field("file", "NotAFolder"), Field("exact", "complete", false) ];
        var mapped = Compile(manifest).Map(new(folder.Path));
        Assert.Equal(ProfileMapStatus.Success, mapped.Status);
        Assert.Equal(true, mapped.Item!.Values["complete"]);
        Assert.Equal(false, mapped.Item.Values["deep"]); Assert.Equal(false, mapped.Item.Values["file"]);
        Assert.Equal(false, mapped.Item.Values["exact"]);
        Assert.Equal(ProfileFieldResourceState.Present, mapped.Item.Resources["complete"].State);
        Assert.Equal("Complete", mapped.Item.Resources["complete"].FileName);
        Assert.Equal(ProfileFieldResourceState.Missing, mapped.Item.Resources["deep"].State);
    }

    [Fact]
    public void Unavailable_folder_is_an_error_not_false_and_path_row_is_preserved()
    {
        using var folder = new Folder(); var manifest = Definition(); manifest.ChildDirectoryFields = [Field("ready", "Ready")];
        var mapped = Compile(manifest).Map(new(Path.Combine(folder.Path, "does-not-exist")));
        Assert.Equal(ProfileMapStatus.Success, mapped.Status); Assert.Null(mapped.Item!.Values["ready"]);
        Assert.Equal(ProfileFieldResourceState.Error, mapped.Item.Resources["ready"].State);
        Assert.NotEmpty(mapped.Item.FieldErrors["ready"]);
        Assert.NotNull(mapped.Item.Values["path"]);
    }

    [Fact]
    public void Many_irrelevant_siblings_do_not_change_literal_direct_child_matching()
    {
        using var folder = new Folder();
        for (var index = 0; index < 512; index++) Directory.CreateDirectory(Path.Combine(folder.Path, $"irrelevant-{index:D4}"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "READY"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "nested", "OnlyNested"));
        var manifest = Definition(); manifest.ChildDirectoryFields =
        [Field("case-insensitive", "ready"), Field("case-sensitive", "ready", false), Field("nested", "OnlyNested")];
        var row = Compile(manifest).Map(new(folder.Path)).Item!;
        Assert.Equal(true, row.Values["case-insensitive"]);
        Assert.Equal(false, row.Values["case-sensitive"]);
        Assert.Equal(false, row.Values["nested"]);
        Assert.Empty(row.FieldErrors);
    }

    [Theory]
    [InlineData("../escape")][InlineData("..")][InlineData("C:\\escape")][InlineData("a*b")]
    [InlineData("name ")][InlineData("NUL")][InlineData("COM1")]
    public void Child_names_reject_paths_patterns_and_ambiguous_names(string name)
    {
        var manifest = Definition(); manifest.ChildDirectoryFields = [Field("ready", name)];
        Assert.False(Review(manifest).IsValid);
    }

    [Theory]
    [InlineData(-1)][InlineData(1025)]
    public void Depth_is_finite_and_validated(int depth)
    { var manifest = Definition(); manifest.MaxScanDepth = depth; Assert.False(Review(manifest).IsValid); }

    [Fact]
    public void Auxiliary_fields_share_the_stable_id_namespace()
    {
        var manifest = Definition(); manifest.ChildDirectoryFields = [Field("path", "Ready")];
        Assert.False(Review(manifest).IsValid);
    }

    [Fact]
    public void Required_missing_and_oversized_text_fields_keep_rows_and_provenance()
    {
        using var folder = new Folder(); File.WriteAllText(Path.Combine(folder.Path, "large.txt"), "too large");
        var manifest = Definition(); manifest.TextFileFields =
        [ new() { FieldId = "missing", Required = true, FileNamePattern = "missing\\.txt" },
          new() { FieldId = "large", FileNamePattern = "large\\.txt", MaxBytes = 1 } ];
        var mapped = Compile(manifest).Map(new(folder.Path));
        Assert.Equal(ProfileMapStatus.Success, mapped.Status);
        Assert.Null(mapped.Item!.Values["missing"]); Assert.Null(mapped.Item.Values["large"]);
        Assert.Equal(ProfileFieldResourceState.Missing, mapped.Item.Resources["missing"].State);
        Assert.Equal(ProfileFieldResourceState.Error, mapped.Item.Resources["large"].State);
        Assert.Equal("large.txt", mapped.Item.Resources["large"].FileName);
        Assert.Equal(2, mapped.Item.FieldErrors.Count);
    }

    [Fact]
    public void Text_resources_capture_encoding_bom_newline_and_content_version()
    {
        using var folder = new Folder(); File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "hello\r\nworld", new UTF8Encoding(true));
        var manifest = Definition(); manifest.TextFileFields = [new() { FieldId = "notes", FileNamePattern = "notes\\.txt" }];
        var profile = Compile(manifest); var mapped = profile.Map(new(folder.Path));
        var info = mapped.Item!.Resources["notes"];
        Assert.Equal(ProfileFieldResourceState.Present, info.State); Assert.Equal("utf-8", info.EncodingName);
        Assert.True(info.HasBom); Assert.Equal("\r\n", info.NewLine); Assert.Equal(64, info.Version!.Length);
        Assert.Equal(info, profile.ReadTextColumn(folder.Path, "notes").Resource);
    }

    [Fact]
    public void Path_capture_errors_still_invalidate_the_row()
    {
        var manifest = Definition(); manifest.Fields![0].Kind = ProfileFieldValueKind.Int32;
        using var folder = new Folder(); Assert.Equal(ProfileMapStatus.Invalid, Compile(manifest).Map(new(folder.Path)).Status);
    }
    private static ProfileChildDirectoryFieldManifest Field(string id, string name, bool ignoreCase = true) =>
        new() { FieldId = id, Header = id, DirectoryName = name, IgnoreCase = ignoreCase };
    private static ProfileManifest Definition() => new()
    {
        ContractVersion = ProfileContract.CurrentMajor, Kind = ProfileKind.Declarative, Id = "resource-test", Version = "1.0", DisplayName = "Resources",
        Fields = [new() { FieldId = "path", Header = "Path", GroupName = "path", Required = true }],
        Rules = [new() { Id = "all", Pattern = "(?<path>.+)" }],
    };
    private static ProfileDefinitionReview Review(ProfileManifest manifest)
    { var services = new ServiceCollection(); services.AddProfileRuntime(); using var provider = services.BuildServiceProvider(); return provider.GetRequiredService<IProfileDefinitionCompiler>().Validate(manifest); }
    private static ILoadedProfile Compile(ProfileManifest manifest)
    { var review = Review(manifest); Assert.True(review.IsValid, string.Join("\n", review.Diagnostics.Select(d => d.Message))); return review.Profile!; }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fe-resource-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
