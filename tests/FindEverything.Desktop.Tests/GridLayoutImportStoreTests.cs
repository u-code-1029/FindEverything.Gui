using FindEverything.Desktop.Configuration;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class PortableWorkspaceStoreTests
{
    [Fact]
    public async Task Imported_layout_publishes_one_notification_and_rejects_stale_UI_saves()
    {
        using var fixture = new Fixture();
        var store = fixture.Layouts();
        var original = new GridColumnLayout("name", 0, 200, GridColumnWidthMode.Manual);
        store.Save("profile", [original]);
        var uiRevision = store.GetRevision("profile");
        var notifications = new List<string>();
        store.Changed += (_, change) => notifications.Add(change.ProfileId);
        store.Changed += (_, _) => throw new InvalidOperationException("A view observer failed.");
        var imported = original with { Width = 420, IsHidden = true };

        Assert.True(await store.TryReplaceAsync("profile", store.Get("profile"), [imported]));
        Assert.Equal(["profile"], notifications);
        Assert.False(store.TrySave("profile", [original], uiRevision));
        Assert.Equal(imported, store.Get("profile")["name"]);
        Assert.True(store.TrySave("profile", [imported with { Width = 430 }], store.GetRevision("profile")));
        await store.StopAsync(default);
        Assert.Equal(430, fixture.Layouts().Get("profile")["name"].Width);
        Assert.True(fixture.Layouts().Get("profile")["name"].IsHidden);
    }
}
