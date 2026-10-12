using System.IO;
using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileUserInputValueStoreTests
{
    [Fact]
    public async Task Values_survive_a_new_store_instance_and_are_isolated_by_full_key()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-user-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new AppPaths(directory, Path.Combine(directory, "settings.json"));
            var first = CreateStore(paths);
            var pathA = Path.GetFullPath(Path.Combine(directory, "a"));
            var pathB = Path.GetFullPath(Path.Combine(directory, "b"));

            await first.SetValueAsync("profile-a", pathA, "note", "first");
            await first.SetValueAsync("profile-a", pathA, "owner", "Ada");
            await first.SetValueAsync("profile-a", pathB, "note", "second");
            await first.SetValueAsync("profile-b", pathA, "note", "other profile");

            var reloaded = CreateStore(paths);
            var values = reloaded.GetValues("profile-a", pathA);
            Assert.Equal("first", values["note"]);
            Assert.Equal("Ada", values["owner"]);
            Assert.Equal("second", reloaded.GetValues("profile-a", pathB)["note"]);
            Assert.Equal("other profile", reloaded.GetValues("profile-b", pathA)["note"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Explicit_empty_value_survives_and_overrides_a_profile_default()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"findeverything-user-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new AppPaths(directory, Path.Combine(directory, "settings.json"));
            var absolutePath = Path.GetFullPath(Path.Combine(directory, "item"));
            await CreateStore(paths).SetValueAsync("profile-a", absolutePath, "note", string.Empty);

            var values = CreateStore(paths).GetValues("profile-a", absolutePath);
            Assert.True(values.ContainsKey("note"));
            Assert.Equal(string.Empty, values["note"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProfileUserInputValueStore CreateStore(AppPaths paths) =>
        new(paths, NullLogger<ProfileUserInputValueStore>.Instance);
}
