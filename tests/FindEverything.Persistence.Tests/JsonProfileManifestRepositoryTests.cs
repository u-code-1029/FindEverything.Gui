using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class JsonProfileManifestRepositoryTests
{
    [Fact]
    public async Task Staged_validation_failure_preserves_the_existing_file_and_removes_staging()
    {
        var root = Path.Combine(Path.GetTempPath(), $"profile-repository-{Guid.NewGuid():N}");
        try
        {
            using var repository = new JsonProfileManifestRepository(root, NullLogger.Instance);
            var initial = new ProfileManifest { Id = "sample", Kind = ProfileKind.Declarative, DisplayName = "Original" };
            Assert.True((await repository.WriteAsync(initial, null, Validate)).FileSaved);
            var entry = Assert.Single(await repository.ListAsync());
            var originalBytes = await File.ReadAllBytesAsync(entry.ManifestPath);
            var loaded = Assert.IsType<ProfileManifest>(await repository.LoadAsync(entry));
            loaded.DisplayName = "Changed";
            var validations = 0;

            var result = await repository.WriteAsync(loaded, "sample", manifest =>
                ++validations == 1 ? Validate(manifest) : new(null,
                    [new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, "test_rejection", "Rejected staged data")]));

            Assert.False(result.FileSaved);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(entry.ManifestPath));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(entry.ManifestPath)!, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task External_edit_after_loading_is_never_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), $"profile-repository-{Guid.NewGuid():N}");
        try
        {
            using var repository = new JsonProfileManifestRepository(root, NullLogger.Instance);
            var manifest = new ProfileManifest { Id = "sample", Kind = ProfileKind.Declarative };
            Assert.True((await repository.WriteAsync(manifest, null, Validate)).FileSaved);
            var entry = Assert.Single(await repository.ListAsync());
            _ = await repository.LoadAsync(entry);
            await File.AppendAllTextAsync(entry.ManifestPath, "\n");
            var externalBytes = await File.ReadAllBytesAsync(entry.ManifestPath);

            var result = await repository.WriteAsync(manifest, "sample", Validate);

            Assert.False(result.FileSaved);
            Assert.Contains(result.Review.Diagnostics, diagnostic => diagnostic.Code == "profile_changed_externally");
            Assert.Equal(externalBytes, await File.ReadAllBytesAsync(entry.ManifestPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ProfileDefinitionReview Validate(ProfileManifest manifest) => new(new LoadedProfile(manifest.Id!), []);

    private sealed class LoadedProfile(string id) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(id, "1.0.0", id, ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }
}
