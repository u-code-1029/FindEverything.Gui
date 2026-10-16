using System.ComponentModel.DataAnnotations;
using FindEverything.Application.Options;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class WorkspaceOptionsTests
{
    [Fact]
    public void Entry_paths_default_to_unspecified_and_an_empty_profile_list_is_valid()
    {
        var options = new WorkspaceOptions();
        Assert.Null(options.ProfileEntryPaths);
        Validate(options);

        options.ProfileEntryPaths = new() { ["profile"] = [] };
        Validate(options);
        Assert.Empty(options.ProfileEntryPaths["profile"]);
    }

    [Fact]
    public void Entry_paths_are_validated_without_normalizing_or_replacing_the_input()
    {
        var entryPaths = new List<string>
        {
            @"C:\Does not need to exist\Projects",
            @"\\192.0.2.10\Team Share\Projects",
            Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "unmounted-profile-entry-path"),
            @"D:/Another Entry",
        };
        var options = new WorkspaceOptions
        {
            RootPath = @"E:\Independent file search root",
            ProfileEntryPaths = new() { ["profile"] = entryPaths },
        };
        var original = entryPaths.ToArray();

        Validate(options);

        Assert.Same(entryPaths, options.ProfileEntryPaths["profile"]);
        Assert.Equal(original, entryPaths);
        Assert.Equal(@"E:\Independent file search root", options.RootPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/folder")]
    [InlineData(@"C:relative")]
    [InlineData(@"\root-relative")]
    [InlineData(@"\\server")]
    [InlineData(@"\\?\C:\DevicePath")]
    [InlineData(@"\\.\C:\DevicePath")]
    [InlineData("C:\\bad\npath")]
    public void Entry_paths_reject_empty_relative_incomplete_network_and_device_paths(string path)
    {
        AssertInvalid(new() { ProfileEntryPaths = new() { ["profile"] = [path] } });
    }

    [Fact]
    public void Entry_paths_reject_null_lists_and_null_items()
    {
        AssertInvalid(new() { ProfileEntryPaths = new() { ["profile"] = null! } });
        AssertInvalid(new() { ProfileEntryPaths = new() { ["profile"] = [null!] } });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Entry_paths_require_a_profile_id(string id)
    {
        AssertInvalid(new() { ProfileEntryPaths = new() { [id] = [] } });
    }

    [Fact]
    public void Entry_path_profile_ids_are_bounded_and_unique_ignoring_case_and_outer_spaces()
    {
        AssertInvalid(new()
        {
            ProfileEntryPaths = new() { [new string('p', WorkspaceOptions.MaximumProfileEntryPathProfileIdLength + 1)] = [] },
        });
        AssertInvalid(new()
        {
            ProfileEntryPaths = new(StringComparer.Ordinal)
            {
                ["profile"] = [],
                [" PROFILE "] = [],
            },
        });
    }

    [Fact]
    public void Entry_path_collections_and_path_lengths_are_bounded()
    {
        AssertInvalid(new()
        {
            ProfileEntryPaths = Enumerable.Range(0, WorkspaceOptions.MaximumProfileEntryPathProfiles + 1)
                .ToDictionary(index => $"profile-{index}", _ => new List<string>()),
        });
        AssertInvalid(new()
        {
            ProfileEntryPaths = new()
            {
                ["profile"] = Enumerable.Repeat(@"C:\Data", ProfileWorkspaceManifestValidator.MaximumEntryPaths + 1).ToList(),
            },
        });
        AssertInvalid(new()
        {
            ProfileEntryPaths = new()
            {
                ["profile"] = ["C:\\" + new string('p', ProfileWorkspaceManifestValidator.MaximumEntryPathLength)],
            },
        });
    }

    [Fact]
    public void Entry_path_profile_and_path_counts_accept_the_documented_limits()
    {
        var options = new WorkspaceOptions
        {
            ProfileEntryPaths = Enumerable.Range(0, WorkspaceOptions.MaximumProfileEntryPathProfiles)
                .ToDictionary(index => $"profile-{index}", _ => Enumerable.Range(0, ProfileWorkspaceManifestValidator.MaximumEntryPaths)
                    .Select(pathIndex => $@"C:\Projects\{pathIndex}").ToList()),
        };

        Validate(options);
        Validate(new()
        {
            ProfileEntryPaths = new() { [new string('p', WorkspaceOptions.MaximumProfileEntryPathProfileIdLength)] = [] },
        });
    }

    [Fact]
    public void Entry_paths_bind_from_settings_without_requiring_database_paths()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspace:RootPath"] = @"D:\Files",
            ["Workspace:ProfileEntryPaths:profile:0"] = @"C:\Projects",
            ["Workspace:ProfileEntryPaths:profile:1"] = @"\\server\share\Projects",
        }).Build();
        using var provider = new ServiceCollection().AddFindEverythingApplication(configuration).BuildServiceProvider();

        var options = provider.GetRequiredService<IValidatedSettingsState<WorkspaceOptions>>().Current;

        Assert.Equal([@"C:\Projects", @"\\server\share\Projects"], options.ProfileEntryPaths!["profile"]);
        Assert.Equal(@"D:\Files", options.RootPath);
        Assert.Null(options.ProfileDatabasePaths);
    }

    private static void Validate(WorkspaceOptions options) =>
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);

    private static void AssertInvalid(WorkspaceOptions options)
    {
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(WorkspaceOptions.ProfileEntryPaths)));
    }
}
