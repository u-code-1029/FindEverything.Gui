using FindEverything.Application.Profiles;
using FindEverything.Desktop.Configuration;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Services;

public sealed record EditableProfileSummary(
    string Id,
    string DisplayName,
    string ManifestPath)
{
    public override string ToString() => DisplayName;
}

public sealed record ProfileSaveResult(
    bool FileSaved,
    bool Applied,
    ProfileDefinitionReview Review,
    ProfileCatalogSnapshot? Snapshot);

public interface IProfileAuthoringService
{
    string UserProfilesDirectory { get; }

    Task<IReadOnlyList<EditableProfileSummary>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ProfileManifest?> LoadAsync(
        EditableProfileSummary profile,
        CancellationToken cancellationToken = default);

    ProfileDefinitionReview Validate(ProfileManifest manifest);

    ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath);

    Task<ProfileSaveResult> SaveAndApplyAsync(
        ProfileManifest manifest,
        string? originalProfileId,
        CancellationToken cancellationToken = default);
}

/// <summary>Presentation compatibility adapter over the UI-independent authoring workflow.</summary>
public sealed class ProfileAuthoringService : IProfileAuthoringService, IDisposable
{
    private readonly IProfileAuthoringWorkflow _workflow;
    private readonly IDisposable? _ownedRepository;
    private readonly bool _ownsWorkflow;

    public ProfileAuthoringService(IProfileAuthoringWorkflow workflow) => _workflow = workflow;

    // Retain the established constructor for external composition and isolated tests.
    // The production composition root injects the registered workflow instead.
    public ProfileAuthoringService(AppPaths paths, IProfileDefinitionCompiler definitionCompiler,
        IProfileCatalog catalog, IProfileCatalogPublisher catalogPublisher, ILogger<ProfileAuthoringService> logger)
    {
        var repository = new JsonProfileManifestRepository(paths.UserProfilesDirectory, logger);
        _ownedRepository = repository;
        _workflow = new ProfileAuthoringWorkflow(repository, definitionCompiler, catalog, catalogPublisher);
        _ownsWorkflow = true;
    }

    public string UserProfilesDirectory => _workflow.UserProfilesDirectory;

    public async Task<IReadOnlyList<EditableProfileSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _workflow.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(static profile => new EditableProfileSummary(profile.Id, profile.DisplayName, profile.ManifestPath)).ToArray();

    public Task<ProfileManifest?> LoadAsync(EditableProfileSummary profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _workflow.LoadAsync(new(profile.Id, profile.DisplayName, profile.ManifestPath), cancellationToken);
    }

    public ProfileDefinitionReview Validate(ProfileManifest manifest) => _workflow.Validate(manifest);

    public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath) => _workflow.Test(manifest, samplePath);

    public async Task<ProfileSaveResult> SaveAndApplyAsync(ProfileManifest manifest, string? originalProfileId,
        CancellationToken cancellationToken = default)
    {
        var result = await _workflow.SaveAndApplyAsync(manifest, originalProfileId, cancellationToken).ConfigureAwait(false);
        return new(result.FileSaved, result.Applied, result.Review, result.Snapshot);
    }

    public void Dispose()
    {
        if (_ownsWorkflow && _workflow is IDisposable disposable) disposable.Dispose();
        _ownedRepository?.Dispose();
    }
}
