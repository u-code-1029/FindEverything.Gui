using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace FindEverything.Profile.Runtime;

internal sealed class ProfileCatalog(
    ILogger<ProfileCatalog> logger) : IProfileCatalog, IProfileCatalogPublisher
{
    private ProfileCatalogSnapshot _current = ProfileCatalogSnapshot.Empty;

    public ProfileCatalogSnapshot Current => Volatile.Read(ref _current);

    public event EventHandler<ProfileCatalogChangedEventArgs>? Changed;

    public void Publish(ProfileCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = Interlocked.Exchange(ref _current, snapshot);
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new ProfileCatalogChangedEventArgs(previous, snapshot);
        foreach (EventHandler<ProfileCatalogChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "A profile catalog change subscriber failed.");
            }
        }
    }
}

internal sealed class ProfileResolver : IProfileResolver
{
    private readonly IProfileCatalog _catalog;

    public ProfileResolver(IProfileCatalog catalog)
    {
        _catalog = catalog;
    }

    public bool TryResolve(
        string profileId,
        [NotNullWhen(true)] out ILoadedProfile? profile) =>
        _catalog.Current.TryGetProfile(profileId, out profile);
}
