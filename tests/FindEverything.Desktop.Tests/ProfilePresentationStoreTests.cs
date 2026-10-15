using System.IO;
using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfilePresentationStoreTests
{
    [Fact]
    public async Task Null_entries_and_case_collisions_in_layout_do_not_block_startup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"findeverything-layout-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new AppPaths(directory, "unused.json");
        try
        {
            await File.WriteAllTextAsync(paths.GridLayoutFile,
                "{\"empty\":null,\"profile\":{\"bad\":null,\"a\":{\"FieldId\":\"a\",\"DisplayIndex\":1,\"Width\":100}},\"PROFILE\":{\"A\":{\"FieldId\":\"A\",\"DisplayIndex\":2,\"Width\":200}}}");
            var store = new GridLayoutStore(paths, NullLogger<GridLayoutStore>.Instance);
            await store.StartAsync(default);
            Assert.Equal(2, store.Get("profile")["a"].DisplayIndex);
            Assert.Empty(store.Get("empty"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Presets_persist_separately_and_reset_only_the_changed_manual_width()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"findeverything-presentation-{Guid.NewGuid():N}");
        var paths = new AppPaths(directory, Path.Combine(directory, "settings.json"));
        var layout = new GridLayoutStore(paths, NullLogger<GridLayoutStore>.Instance);
        var store = new ProfilePresentationStore(paths, layout, NullLogger<ProfilePresentationStore>.Instance);
        try
        {
            Assert.Empty(await store.LoadAsync("test-profile"));
            layout.Save("test-profile", [new("a", 1, 220, GridColumnWidthMode.Manual, IsHidden: true), new("b", 2, 280, GridColumnWidthMode.Manual)]);
            ProfilePresentationChangedEventArgs? notification = null;
            store.Changed += (_, e) => notification = e;
            await store.SaveAsync("test-profile", new Dictionary<string, ColumnWidthPreset> { ["a"] = ColumnWidthPreset.Wide });
            await layout.FlushAsync();
            var reload = new ProfilePresentationStore(paths, layout, NullLogger<ProfilePresentationStore>.Instance);
            Assert.Equal(ColumnWidthPreset.Wide, (await reload.LoadAsync("test-profile"))["a"]);
            Assert.Equal(GridColumnWidthMode.Auto, layout.Get("test-profile")["a"].WidthMode);
            Assert.True(layout.Get("test-profile")["a"].IsHidden);
            Assert.Equal(GridColumnWidthMode.Manual, layout.Get("test-profile")["b"].WidthMode);
            Assert.Equal(["a"], notification!.ChangedFields);
            Assert.False(File.Exists(Path.Combine(paths.UserProfilesDirectory, "test-profile", "profile.json")));
            Assert.False(File.Exists(paths.ResultsDatabaseFile));
        }
        finally { await layout.StopAsync(default); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Legacy_widths_become_auto_but_column_order_survives()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"findeverything-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new AppPaths(directory, Path.Combine(directory, "settings.json"));
        try
        {
            await File.WriteAllTextAsync(paths.GridLayoutFile, "{\"profile\":{\"name\":{\"FieldId\":\"name\",\"DisplayIndex\":3,\"Width\":777}}}");
            var store = new GridLayoutStore(paths, NullLogger<GridLayoutStore>.Instance);
            var state = store.Get("profile")["name"];
            Assert.Equal(3, state.DisplayIndex);
            Assert.Equal(GridColumnWidthMode.Auto, state.WidthMode);
            Assert.False(state.IsHidden);
            for (var i = 0; i < 50; i++) store.Save("profile", [state with { Width = 200 + i, WidthMode = GridColumnWidthMode.Manual, IsHidden = true }]);
            await store.StopAsync(default);
            var reloaded = new GridLayoutStore(paths, NullLogger<GridLayoutStore>.Instance).Get("profile")["name"];
            Assert.Equal(249, reloaded.Width);
            Assert.Equal(GridColumnWidthMode.Manual, reloaded.WidthMode);
            Assert.True(reloaded.IsHidden);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:escape")]
    public async Task Presentation_store_rejects_path_identifiers(string id)
    {
        var paths = new AppPaths(Path.GetTempPath(), "unused.json");
        var store = new ProfilePresentationStore(paths, new GridLayoutStore(paths, NullLogger<GridLayoutStore>.Instance),
            NullLogger<ProfilePresentationStore>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(id));
    }
}
