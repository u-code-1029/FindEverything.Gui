using System.Diagnostics.CodeAnalysis;

namespace FindEverything.Profile.Runtime;

internal sealed class ProfileCatalog : IProfileCatalog, IProfileCatalogPublisher
{
    private ProfileCatalogSnapshot _current = ProfileCatalogSnapshot.Empty;

    public ProfileCatalogSnapshot Current => Volatile.Read(ref _current);

    public void Publish(ProfileCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref _current, snapshot);
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
