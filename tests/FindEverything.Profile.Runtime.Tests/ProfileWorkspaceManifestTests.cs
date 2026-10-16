using System.Text.Json;
using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfileWorkspaceManifestTests
{
    [Theory]
    [InlineData(@"C:\Data", "c:/data/")]
    [InlineData(@"\\server\share\Data", "//SERVER/share/data/")]
    public void Starting_locations_reject_case_separator_and_trailing_separator_duplicates(string first, string duplicate)
    {
        var manifest = Manifest();
        manifest.Workspace = new() { EntryPaths = [first, duplicate] };
        Assert.Contains(ProfileWorkspaceManifestValidator.Validate(manifest),
            issue => issue.Code == "workspace_entry_paths_invalid");
        manifest.Workspace.EntryPaths = [first, first + "base"];
        Assert.Empty(ProfileWorkspaceManifestValidator.Validate(manifest));
    }
    [Theory]
    [InlineData(@"C:\Does not need to exist")]
    [InlineData(@"\\192.0.2.20\Shared Folder\Data")]
    public void Portable_starting_locations_validate_as_windows_absolute_paths_without_filesystem_access(string path)
    {
        var manifest = Manifest();
        manifest.Workspace = new() { EntryPaths = [path] };
        Assert.Empty(ProfileWorkspaceManifestValidator.Validate(manifest));
        var copy = JsonSerializer.Deserialize<ProfileManifest>(JsonSerializer.Serialize(manifest))!;
        Assert.Equal([path], copy.Workspace!.EntryPaths);
    }

    [Theory]
    [InlineData(@"relative\Data")]
    [InlineData(@"C:relative")]
    [InlineData(@"\\server")]
    [InlineData(@"\\?\C:\Device")]
    [InlineData("")]
    public void Invalid_starting_locations_are_actionable_profile_errors(string path)
    {
        var manifest = Manifest();
        manifest.Workspace = new() { EntryPaths = [path] };
        Assert.Contains(ProfileWorkspaceManifestValidator.Validate(manifest),
            issue => issue.Code == "workspace_entry_paths_invalid");
    }
    [Fact]
    public void Desktop_settings_round_trip_without_changing_parser_descriptor_or_cache_identity()
    {
        using var provider = new ServiceCollection().AddProfileRuntime().BuildServiceProvider();
        var compiler = provider.GetRequiredService<IProfileDefinitionCompiler>();
        var manifest = Manifest();
        var before = compiler.Validate(manifest);
        Assert.True(before.IsValid);
        manifest.Workspace = Workspace();
        var copy = JsonSerializer.Deserialize<ProfileManifest>(JsonSerializer.Serialize(manifest))!;
        var after = compiler.Validate(copy);
        Assert.True(after.IsValid, string.Join("; ", after.Diagnostics.Select(d => d.Message)));
        Assert.Equal(JsonSerializer.Serialize(before.Profile!.Descriptor), JsonSerializer.Serialize(after.Profile!.Descriptor));
        Assert.Equal(before.Profile.CacheIdentity, after.Profile.CacheIdentity);
        Assert.Equal("<", Assert.Single(copy.Workspace!.OutputFormats!).Prefix);
        Assert.Equal(240, Assert.Single(copy.Workspace.Columns!).Width);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("unknown-column")]
    [InlineData("duplicate-column")]
    [InlineData("duplicate-format")]
    [InlineData("missing-selection")]
    [InlineData("oversized-template")]
    [InlineData("invalid-width")]
    [InlineData("null-format")]
    [InlineData("invalid-enum")]
    public void Invalid_portable_settings_are_rejected_before_compilation(string kind)
    {
        var manifest = Manifest(); manifest.Workspace = Workspace();
        var workspace = manifest.Workspace;
        switch (kind)
        {
            case "version": workspace.Version = 2; break;
            case "unknown-column": workspace.Columns![0].FieldId = "absent"; break;
            case "duplicate-column": workspace.Columns!.Add(workspace.Columns[0]); break;
            case "duplicate-format": workspace.OutputFormats!.Add(workspace.OutputFormats[0]); break;
            case "missing-selection": workspace.SelectedOutputFormatId = "absent"; break;
            case "oversized-template": workspace.OutputFormats![0].Template = new string('x', 16385); break;
            case "invalid-width": workspace.Columns![0].Width = double.NaN; break;
            case "null-format": workspace.OutputFormats!.Add(null!); break;
            case "invalid-enum": workspace.Columns![0].WidthPreset = (ProfileColumnWidthPreset)50; break;
        }
        using var provider = new ServiceCollection().AddProfileRuntime().BuildServiceProvider();
        var result = provider.GetRequiredService<IProfileDefinitionCompiler>().Validate(manifest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == "workspace_invalid");
    }

    [Fact]
    public void Legacy_profiles_need_no_workspace_and_oversized_collections_are_rejected()
    {
        Assert.Empty(ProfileWorkspaceManifestValidator.Validate(Manifest()));
        var manifest = Manifest(); manifest.Workspace = Workspace();
        manifest.Workspace.OutputFormats = Enumerable.Range(0, 65).Select(i => new ProfileOutputFormatManifest
        { Id = "format-" + i, DisplayName = "Format", Template = "{FullPath}" }).ToList();
        Assert.NotEmpty(ProfileWorkspaceManifestValidator.Validate(manifest));
    }

    private static ProfileManifest Manifest() => new()
    {
        ContractVersion = ProfileContract.CurrentMajor, Kind = ProfileKind.Declarative, Id = "workspace-test", Version = "1", DisplayName = "Workspace test",
        Fields = [new() { FieldId = "name", GroupName = "name", Header = "Name" }],
        Rules = [new() { Id = "rule", Pattern = "(?<name>.+)" }],
    };

    private static ProfileWorkspaceManifest Workspace() => new()
    {
        OutputFormats = [new() { Id = "name-lines", DisplayName = "Name lines", Template = "{name}", Prefix = "<", Suffix = ">" }],
        SelectedOutputFormatId = "name-lines",
        Columns = [new() { FieldId = "name", DisplayIndex = 0, WidthPreset = ProfileColumnWidthPreset.Wide,
            WidthMode = ProfileColumnWidthMode.Manual, Width = 240, IsHidden = true }],
    };
}
