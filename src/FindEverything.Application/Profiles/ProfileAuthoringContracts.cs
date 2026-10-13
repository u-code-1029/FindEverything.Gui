using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Profiles;

public sealed record ProfileManifestEntry(string Id, string DisplayName, string ManifestPath);

public sealed record ProfileManifestWriteResult(
    bool FileSaved,
    string? SourceDirectory,
    ProfileDefinitionReview Review);

/// <summary>
/// Owns manifest IO and optimistic edit fingerprints. Validation remains a use-case
/// policy and is rerun against the exact staged bytes before atomic replacement.
/// </summary>
public interface IProfileManifestRepository
{
    string UserProfilesDirectory { get; }

    Task<IReadOnlyList<ProfileManifestEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<ProfileManifest?> LoadAsync(ProfileManifestEntry profile, CancellationToken cancellationToken = default);

    string GetProfileDirectory(string profileId);

    Task<ProfileManifestWriteResult> WriteAsync(
        ProfileManifest manifest,
        string? originalProfileId,
        Func<ProfileManifest, ProfileDefinitionReview> validate,
        CancellationToken cancellationToken = default);
}

public sealed record ProfileAuthoringResult(
    bool FileSaved,
    bool Applied,
    ProfileDefinitionReview Review,
    ProfileCatalogSnapshot? Snapshot);

public interface IProfileAuthoringWorkflow
{
    string UserProfilesDirectory { get; }
    Task<IReadOnlyList<ProfileManifestEntry>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProfileManifest?> LoadAsync(ProfileManifestEntry profile, CancellationToken cancellationToken = default);
    ProfileDefinitionReview Validate(ProfileManifest manifest);
    ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath);
    Task<ProfileAuthoringResult> SaveAndApplyAsync(ProfileManifest manifest, string? originalProfileId,
        CancellationToken cancellationToken = default);
}
