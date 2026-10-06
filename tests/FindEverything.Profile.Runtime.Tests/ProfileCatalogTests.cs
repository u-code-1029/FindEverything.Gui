using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Profile.Runtime.Tests;

public sealed class ProfileCatalogTests
{
    [Fact]
    public void Publish_IsolatesSubscribersAndNotifiesTheRemainingHandlers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProfileRuntime();
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IProfileCatalog>();
        var publisher = provider.GetRequiredService<IProfileCatalogPublisher>();
        var notified = 0;
        catalog.Changed += static (_, _) => throw new InvalidOperationException("subscriber failure");
        catalog.Changed += (_, eventArgs) =>
        {
            Assert.Same(ProfileCatalogSnapshot.Empty, eventArgs.Previous);
            notified++;
        };
        var snapshot = new ProfileCatalogSnapshot([], [], DateTimeOffset.UtcNow);

        publisher.Publish(snapshot);

        Assert.Same(snapshot, catalog.Current);
        Assert.Equal(1, notified);
    }
}
