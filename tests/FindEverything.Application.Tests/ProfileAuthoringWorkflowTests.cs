using FindEverything.Application.Profiles;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class ProfileAuthoringWorkflowTests
{
    [Fact]
    public async Task Synchronous_storage_and_compilation_do_not_block_the_caller()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Repository
        {
            BeforeWrite = () =>
            {
                entered.SetResult(Environment.CurrentManagedThreadId);
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            },
        };
        var catalog = new Catalog();
        using var workflow = new ProfileAuthoringWorkflow(repository, new Compiler(), catalog, catalog);
        var pending = workflow.SaveAndApplyAsync(new ProfileManifest { Id = "sample" }, null);
        try
        {
            _ = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Empty(catalog.Current.Profiles);
        }
        finally { release.Set(); }
        Assert.True((await pending).Applied);
    }

    [Fact]
    public async Task Cancellation_after_commit_still_publishes_the_committed_definition()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Repository { AfterCommit = cancellation.Cancel };
        var catalog = new Catalog();
        using var workflow = new ProfileAuthoringWorkflow(repository, new Compiler(), catalog, catalog);

        var result = await workflow.SaveAndApplyAsync(new ProfileManifest { Id = "sample" }, null, cancellation.Token);

        Assert.True(result.FileSaved);
        Assert.True(result.Applied);
        Assert.Same(result.Snapshot, catalog.Current);
        Assert.Equal(1, catalog.PublicationCount);
    }

    [Fact]
    public async Task Duplicate_external_profile_is_rejected_before_repository_commit()
    {
        var repository = new Repository();
        var catalog = new Catalog();
        catalog.Publish(new ProfileCatalogSnapshot([new LoadedProfile("sample")],
            [new ProfilePluginReport(Path.Combine(Path.GetTempPath(), "bundled", "sample"),
                "sample", "Sample", ProfilePluginStatus.Loaded, [])], DateTimeOffset.UtcNow));
        using var workflow = new ProfileAuthoringWorkflow(repository, new Compiler(), catalog, catalog);

        var result = await workflow.SaveAndApplyAsync(new ProfileManifest { Id = "sample" }, null);

        Assert.False(result.FileSaved);
        Assert.False(repository.Committed);
        Assert.Contains(result.Review.Diagnostics, diagnostic => diagnostic.Code == "profile_id_duplicate");
        Assert.Equal(1, catalog.PublicationCount);
    }

    private sealed class Repository : IProfileManifestRepository
    {
        public Action? BeforeWrite { get; init; }
        public Action? AfterCommit { get; init; }
        public bool Committed { get; private set; }
        public string UserProfilesDirectory => Path.Combine(Path.GetTempPath(), "authoring-workflow-test");
        public string GetProfileDirectory(string profileId) => Path.Combine(UserProfilesDirectory, profileId);
        public Task<IReadOnlyList<ProfileManifestEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProfileManifestEntry>>([]);
        public Task<ProfileManifest?> LoadAsync(ProfileManifestEntry profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProfileManifest?>(null);
        public Task<ProfileManifestWriteResult> WriteAsync(ProfileManifest manifest, string? originalProfileId,
            Func<ProfileManifest, ProfileDefinitionReview> validate, CancellationToken cancellationToken = default)
        {
            BeforeWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var review = validate(manifest);
            Committed = review.IsValid;
            if (Committed) AfterCommit?.Invoke();
            return Task.FromResult(new ProfileManifestWriteResult(Committed, GetProfileDirectory(manifest.Id!), review));
        }
    }

    private sealed class Compiler : IProfileDefinitionCompiler
    {
        public ProfileDefinitionReview Validate(ProfileManifest manifest) => new(new LoadedProfile(manifest.Id!), []);
        public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath) => new(Validate(manifest), null);
    }

    private sealed class LoadedProfile(string id) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(id, "1.0.0", id, ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }

    private sealed class Catalog : IProfileCatalog, IProfileCatalogPublisher
    {
        public ProfileCatalogSnapshot Current { get; private set; } = ProfileCatalogSnapshot.Empty;
        public int PublicationCount { get; private set; }
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
        public void Publish(ProfileCatalogSnapshot snapshot) { Current = snapshot; PublicationCount++; }
    }
}
