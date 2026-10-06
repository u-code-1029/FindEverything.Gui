using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace FindEverything.Application.Tests;

public sealed class CatalogServiceTests
{
    [Fact]
    public async Task LoadExisting_maps_matches_and_keeps_invalid_items_separate()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-root"));
        var goodPath = Path.Combine(root, "good");
        var badPath = Path.Combine(root, "bad");
        var services = new ServiceCollection();
        services.AddFindEverythingApplication(new ConfigurationBuilder().Build());
        services.AddSingleton<IIndexSessionFactory>(new FakeSessionFactory([
            new IndexedDirectory(goodPath, "good", root, DateTimeOffset.MinValue, DateTimeOffset.MinValue, false),
            new IndexedDirectory(badPath, "bad", root, DateTimeOffset.MinValue, DateTimeOffset.MinValue, true),
        ]));
        services.AddSingleton<IProfileResolver>(new FakeProfileResolver(new FakeProfile()));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ICatalogService>();

        var result = await service.LoadExistingAsync(new CatalogRequest("test", root, "index.db"));

        var item = Assert.Single(result.Items);
        Assert.Equal(goodPath, item.FullPath);
        Assert.Equal("good", item.Values["name"]);
        var invalid = Assert.Single(result.InvalidItems);
        Assert.Equal(badPath, invalid.FullPath);
        Assert.True(result.HasPendingScopes);
    }

    private sealed class FakeSessionFactory(IReadOnlyList<IndexedDirectory> directories) : IIndexSessionFactory
    {
        public IIndexSession Create(string databasePath) => new FakeSession(databasePath, directories);
    }

    private sealed class FakeSession(string databasePath, IReadOnlyList<IndexedDirectory> directories) : IIndexSession
    {
        public string DatabasePath { get; } = databasePath;

        public Task<IndexScanReport> ScanAsync(
            IndexScanRequest request,
            IProgress<IndexScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DirectorySearchResult> SearchDirectoriesAsync(
            DirectorySearchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DirectorySearchResult(directories, HasPendingScopes: false));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProfileResolver(ILoadedProfile profile) : IProfileResolver
    {
        public bool TryResolve(
            string profileId,
            [NotNullWhen(true)] out ILoadedProfile? resolved)
        {
            resolved = string.Equals(profileId, profile.Descriptor.Id, StringComparison.OrdinalIgnoreCase)
                ? profile
                : null;
            return resolved is not null;
        }
    }

    private sealed class FakeProfile : ILoadedProfile
    {
        private static readonly ProfileFieldDescriptor NameField = new(
            "name",
            "name",
            "이름",
            10,
            true,
            ProfileFieldValueKind.String,
            true,
            null,
            null);

        public ProfileDescriptor Descriptor { get; } = new(
            "test",
            "1.0.0",
            "Test",
            ProfileCandidateKind.Directory,
            ProfilePathInput.Relative,
            [NameField],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            if (candidate.RelativePath == "bad")
            {
                return ProfileMapResult.Invalid([
                    new ProfileMappingIssue("bad_path", "name", "Bad test path."),
                ]);
            }

            var values = new Dictionary<string, object?> { ["name"] = candidate.RelativePath };
            return ProfileMapResult.Success(new MappedProfileItem(
                "test",
                candidate.FullPath,
                candidate.RelativePath,
                "test-rule",
                new object(),
                values));
        }
    }
}
