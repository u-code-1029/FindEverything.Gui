using System.IO;
using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class PortableWorkspaceStoreTests
{
    [Fact]
    public async Task Profile_format_batch_preserves_shared_and_other_profile_formats_and_notifies_once()
    {
        using var fixture = new Fixture();
        var store = fixture.Formats();
        var shared = Format("shared", null);
        var other = Format("other-format", "other");
        var original = Format("original", "target");
        store.Save(shared);
        store.Save(other);
        store.Save(original);
        var notifications = 0;
        store.Changed += (_, _) => notifications++;

        var imported = Format("portable.new", "target");
        await store.ReplaceProfileFormatsAsync("target", [imported]);

        Assert.Equal(1, notifications);
        Assert.Contains(shared, fixture.Formats().GetAll());
        Assert.Contains(other, fixture.Formats().GetAll());
        Assert.Contains(imported, fixture.Formats().GetAll());
        Assert.DoesNotContain(original, fixture.Formats().GetAll());
        Assert.True(await store.TryReplaceProfileFormatsAsync("target", [imported], [original]));
        Assert.Contains(original, fixture.Formats().GetAll());
    }

    [Fact]
    public async Task Profile_format_batch_rejects_foreign_ID_collisions_without_partial_changes()
    {
        using var fixture = new Fixture();
        var store = fixture.Formats();
        store.Save(Format("occupied", null));
        store.Save(Format("original", "target"));
        var before = File.ReadAllBytes(fixture.Paths.SelectionOutputFormatsFile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceProfileFormatsAsync("target",
            [Format("new", "target"), Format("occupied", "target")]));

        Assert.Equal(before, File.ReadAllBytes(fixture.Paths.SelectionOutputFormatsFile));
        Assert.DoesNotContain(store.GetAll(), format => format.Id == "new");
    }

    [Fact]
    public async Task Format_rollback_does_not_overwrite_a_later_external_edit()
    {
        using var fixture = new Fixture();
        var store = fixture.Formats();
        var imported = Format("imported", "target");
        await store.ReplaceProfileFormatsAsync("target", [imported]);
        var edited = imported with { Template = "{Name}" };
        fixture.Formats().Save(edited);

        Assert.False(await store.TryReplaceProfileFormatsAsync("target", [imported], []));
        Assert.Contains(edited, fixture.Formats().GetAll());
    }

    [Fact]
    public async Task Format_batch_cancellation_and_observer_failure_do_not_corrupt_saved_state()
    {
        using var fixture = new Fixture();
        var store = fixture.Formats();
        var original = Format("original", "target");
        store.Save(original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReplaceProfileFormatsAsync("target", [], cancellation.Token));
        Assert.Contains(original, store.GetAll());
        store.Changed += (_, _) => throw new InvalidOperationException("Observer failed.");
        await store.ReplaceProfileFormatsAsync("target", [Format("new", "target")]);
        Assert.Contains(fixture.Formats().GetAll(), format => format.Id == "new");
    }

    [Fact]
    public async Task Durable_layout_write_merges_pending_local_and_external_other_profile_edits()
    {
        using var fixture = new Fixture();
        var store = fixture.Layouts();
        var pending = new GridColumnLayout("pending-field", 0, 170, GridColumnWidthMode.Manual);
        store.Save("pending", [pending]);
        await fixture.Layouts().SaveAsync("external", [new("external-field", 0, 200)]);
        var target = new GridColumnLayout("target-field", 0, 250, GridColumnWidthMode.Manual);

        await store.SaveAsync("target", [target]);
        await store.StopAsync(default);

        var reloaded = fixture.Layouts();
        Assert.Equal(pending, reloaded.Get("pending")[pending.FieldId]);
        Assert.Single(reloaded.Get("external"));
        Assert.Equal(target, reloaded.Get("target")[target.FieldId]);
    }

    [Fact]
    public async Task Layout_conditional_rollback_preserves_a_later_external_edit()
    {
        using var fixture = new Fixture();
        var store = fixture.Layouts();
        var imported = new GridColumnLayout("field", 0, 220, GridColumnWidthMode.Manual);
        await store.SaveAsync("target", [imported]);
        var expected = store.Get("target");
        var edited = imported with { Width = 333 };
        await fixture.Layouts().SaveAsync("target", [edited]);

        Assert.False(await store.TryReplaceAsync("target", expected, []));
        Assert.Equal(edited, store.Get("target")["field"]);
        Assert.Equal(edited, fixture.Layouts().Get("target")["field"]);
    }

    [Fact]
    public async Task Durable_layout_failure_is_reported_without_publishing_unwritten_layout()
    {
        using var fixture = new Fixture();
        var store = fixture.Layouts();
        var original = new GridColumnLayout("field", 0, 220, GridColumnWidthMode.Manual);
        await store.SaveAsync("target", [original]);
        File.Delete(fixture.Paths.GridLayoutFile);
        Directory.CreateDirectory(fixture.Paths.GridLayoutFile);

        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync("target", [original with { Width = 999 }]));

        Assert.Equal(original, store.Get("target")["field"]);
    }

    [Fact]
    public async Task Imported_presets_do_not_reset_manual_layout_and_observers_cannot_abort_commit()
    {
        using var fixture = new Fixture();
        var layout = fixture.Layouts();
        await layout.SaveAsync("target", [new("field", 0, 260, GridColumnWidthMode.Manual)]);
        var store = fixture.Presentation(layout);
        store.Changed += (_, _) => throw new InvalidOperationException("Observer failed.");
        var imported = new Dictionary<string, ColumnWidthPreset> { ["field"] = ColumnWidthPreset.Wide };

        Assert.True(await store.TryReplaceAsync("target", new Dictionary<string, ColumnWidthPreset>(), imported));

        Assert.Equal(ColumnWidthPreset.Wide, (await fixture.Presentation(layout).LoadAsync("target"))["field"]);
        Assert.Equal(GridColumnWidthMode.Manual, layout.Get("target")["field"].WidthMode);
    }

    [Fact]
    public async Task Presentation_conditional_rollback_preserves_later_external_edit()
    {
        using var fixture = new Fixture();
        var layout = fixture.Layouts();
        var store = fixture.Presentation(layout);
        var imported = new Dictionary<string, ColumnWidthPreset> { ["field"] = ColumnWidthPreset.Wide };
        Assert.True(await store.TryReplaceAsync("target", new Dictionary<string, ColumnWidthPreset>(), imported));
        var edited = new Dictionary<string, ColumnWidthPreset> { ["field"] = ColumnWidthPreset.Narrow };
        Assert.True(await fixture.Presentation(layout).TryReplaceAsync("target", imported, edited));

        Assert.False(await store.TryReplaceAsync("target", imported, new Dictionary<string, ColumnWidthPreset>()));
        Assert.Equal(ColumnWidthPreset.Narrow, (await store.LoadAsync("target"))["field"]);
    }

    [Fact]
    public async Task Output_preferences_persist_in_call_order_and_flush_waits_for_pending_selection()
    {
        using var fixture = new Fixture();
        var store = fixture.Preferences();
        var writes = Enumerable.Range(0, 12).Select(index => store.SaveAsync("target", "format-" + index)).ToArray();
        await store.FlushAsync();
        await Task.WhenAll(writes);

        Assert.Equal("format-11", store.Get("target"));
        Assert.Equal("format-11", fixture.Preferences().Get("target"));
        await store.SaveAsync("other", "shared");
        Assert.True(await store.TryReplaceAsync("target", "format-11", null));
        Assert.Null(fixture.Preferences().Get("target"));
        Assert.Equal("shared", fixture.Preferences().Get("other"));
    }

    [Fact]
    public async Task Output_preference_rollback_does_not_overwrite_later_external_selection()
    {
        using var fixture = new Fixture();
        var store = fixture.Preferences();
        await store.SaveAsync("target", "imported");
        await fixture.Preferences().SaveAsync("target", "user-selected");

        Assert.False(await store.TryReplaceAsync("target", "imported", "original"));
        Assert.Equal("user-selected", store.Get("target"));
    }

    private static SelectionOutputFormatDefinition Format(string id, string? profileId) =>
        new(id, id, "{FullPath}", "\n", profileId);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"findeverything-portable-stores-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            Paths = new(directory, Path.Combine(directory, "settings.json"));
        }
        public AppPaths Paths { get; }
        public SelectionOutputFormatStore Formats() => new(Paths, NullLogger<SelectionOutputFormatStore>.Instance);
        public GridLayoutStore Layouts() => new(Paths, NullLogger<GridLayoutStore>.Instance);
        public ProfilePresentationStore Presentation(IGridLayoutStore layout) => new(Paths, layout, NullLogger<ProfilePresentationStore>.Instance);
        public ProfileOutputPreferenceStore Preferences() => new(Paths, NullLogger<ProfileOutputPreferenceStore>.Instance);
        public void Dispose() { if (Directory.Exists(Paths.LocalDataDirectory)) Directory.Delete(Paths.LocalDataDirectory, true); }
    }
}
