using System.IO;
using System.Text.Json;
using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SelectionOutputFormatStoreTests
{
    [Fact]
    public void Built_in_format_is_always_available_without_creating_a_file()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        var store = CreateStore(paths);

        var format = Assert.Single(store.GetAll());

        Assert.Equal(SelectionOutputFormatDefaults.FullPathLinesId, format.Id);
        Assert.True(format.IsBuiltIn);
        Assert.False(File.Exists(paths.SelectionOutputFormatsFile));
    }

    [Fact]
    public void Custom_formats_persist_and_profile_scopes_are_filtered()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        var store = CreateStore(paths);
        var changeCount = 0;
        store.Changed += (_, _) => changeCount++;
        store.Save(new SelectionOutputFormatDefinition(
            "common",
            "공통",
            "{Name}",
            ", ",
            null));
        store.Save(new SelectionOutputFormatDefinition(
            "projects",
            "프로젝트",
            "{Field:Client}: {FullPath}",
            Environment.NewLine,
            "sample-projects"));

        var reloaded = CreateStore(paths);

        Assert.Equal(2, changeCount);
        Assert.Equal(3, reloaded.GetAll().Count);
        Assert.Equal(
            [SelectionOutputFormatDefaults.FullPathLinesId, "common", "projects"],
            reloaded.GetApplicable("sample-projects").Select(static format => format.Id));
        Assert.Equal(
            [SelectionOutputFormatDefaults.FullPathLinesId, "common"],
            reloaded.GetApplicable("another-profile").Select(static format => format.Id));
        Assert.True(reloaded.Delete("projects"));
        Assert.False(reloaded.Delete(SelectionOutputFormatDefaults.FullPathLinesId));
        Assert.DoesNotContain(CreateStore(paths).GetAll(), static format => format.Id == "projects");
    }

    [Fact]
    public void Invalid_entries_are_ignored_without_hiding_the_built_in_format()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreatePaths(directory.Path);
        File.WriteAllText(
            paths.SelectionOutputFormatsFile,
            JsonSerializer.Serialize(new
            {
                version = 1,
                formats = new[]
                {
                    new
                    {
                        id = "",
                        displayName = "잘못된 포맷",
                        template = "{FullPath}",
                        itemSeparator = "\n",
                        profileId = (string?)null,
                    },
                },
            }));

        var formats = CreateStore(paths).GetAll();

        Assert.Single(formats);
        Assert.True(formats[0].IsBuiltIn);
    }

    private static SelectionOutputFormatStore CreateStore(AppPaths paths) =>
        new(paths, NullLogger<SelectionOutputFormatStore>.Instance);

    private static AppPaths CreatePaths(string directory) =>
        new(directory, Path.Combine(directory, "appsettings.user.json"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"findeverything-output-format-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
