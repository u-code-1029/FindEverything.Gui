using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Profiles;

/// <summary>Compiles, commits and publishes one profile without depending on a UI or storage implementation.</summary>
public sealed class ProfileAuthoringWorkflow(
    IProfileManifestRepository repository,
    IProfileDefinitionCompiler compiler,
    IProfileCatalog catalog,
    IProfileCatalogPublisher publisher) : IProfileAuthoringWorkflow, IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public string UserProfilesDirectory => repository.UserProfilesDirectory;

    public Task<IReadOnlyList<ProfileManifestEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => repository.ListAsync(cancellationToken), cancellationToken);

    public Task<ProfileManifest?> LoadAsync(ProfileManifestEntry profile, CancellationToken cancellationToken = default) =>
        Task.Run(() => repository.LoadAsync(profile, cancellationToken), cancellationToken);

    public ProfileDefinitionReview Validate(ProfileManifest manifest) => compiler.Validate(manifest);

    public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath) => compiler.Test(manifest, samplePath);

    public Task<ProfileAuthoringResult> SaveAndApplyAsync(ProfileManifest manifest, string? originalProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Serialization, compilation and filesystem preflight may all complete
        // synchronously before the first IO await. Never run those on the caller's UI thread.
        return Task.Run(() => SaveCoreAsync(manifest, originalProfileId, cancellationToken), cancellationToken);
    }

    private async Task<ProfileAuthoringResult> SaveCoreAsync(ProfileManifest manifest, string? originalProfileId,
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var write = await repository.WriteAsync(manifest, originalProfileId, ValidateForSave, cancellationToken)
                .ConfigureAwait(false);
            if (!write.FileSaved || write.Review.Profile is null)
                return new(false, false, write.Review, null);

            // The commit has completed: publish that exact result even if the caller
            // cancels now, rather than claiming a committed file was never applied.
            var profile = write.Review.Profile;
            var current = catalog.Current;
            var snapshot = new ProfileCatalogSnapshot(
                current.Profiles.Where(item => !string.Equals(item.Descriptor.Id, profile.Descriptor.Id,
                    StringComparison.OrdinalIgnoreCase)).Append(profile).ToArray(),
                current.Reports.Where(report => !PathsEqual(report.SourceDirectory, write.SourceDirectory!))
                    .Append(new ProfilePluginReport(write.SourceDirectory!, profile.Descriptor.Id,
                        profile.Descriptor.DisplayName, ProfilePluginStatus.Loaded, write.Review.Diagnostics)).ToArray(),
                DateTimeOffset.UtcNow);
            publisher.Publish(snapshot);
            return new(true, true, write.Review, snapshot);
        }
        finally { _saveGate.Release(); }

        ProfileDefinitionReview ValidateForSave(ProfileManifest candidate)
        {
            var review = compiler.Validate(candidate);
            if (!review.IsValid || review.Profile is null) return review;
            var id = review.Profile.Descriptor.Id;
            if (originalProfileId is not null && !string.Equals(id, originalProfileId, StringComparison.OrdinalIgnoreCase))
                return InvalidReview(review, "profile_id_immutable", "저장된 프로필의 ID는 변경할 수 없습니다. 새 프로필로 만들어 주세요.");
            var directory = repository.GetProfileDirectory(id);
            return catalog.Current.Reports.Any(report => string.Equals(report.ProfileId, id, StringComparison.OrdinalIgnoreCase)
                    && !PathsEqual(report.SourceDirectory, directory))
                ? InvalidReview(review, "profile_id_duplicate", $"다른 위치에 같은 프로필 ID가 있습니다: {id}")
                : review;
        }
    }

    private static ProfileDefinitionReview InvalidReview(ProfileDefinitionReview review, string code, string message) =>
        review with { Profile = null, Diagnostics = review.Diagnostics.Append(
            new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, code, message)).ToArray() };

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose() => _saveGate.Dispose();
}
