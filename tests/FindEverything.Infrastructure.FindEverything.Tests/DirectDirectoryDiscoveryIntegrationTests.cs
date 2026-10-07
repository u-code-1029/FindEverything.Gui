using FindEverything.Application;
using FindEverything.Application.Catalog;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FindEverything.Infrastructure.FindEverything.Tests;

public sealed class DirectDirectoryDiscoveryIntegrationTests
{
    [Fact]
    public async Task Discover_maps_terminal_directories_prunes_children_and_does_not_create_an_index()
    {
        using var workspace = new TestWorkspace();
        var projectPath = Path.Combine(workspace.SourcePath, "2026", "0521_Project");
        var childPath = Path.Combine(projectPath, "child");
        var siblingPath = Path.Combine(workspace.SourcePath, "2026", "0522_Sibling");
        Directory.CreateDirectory(childPath);
        Directory.CreateDirectory(siblingPath);

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NoOpLogger<>));
        services
            .AddFindEverythingApplication(configuration)
            .AddFindEverythingInfrastructure()
            .AddProfileRuntime();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var review = provider
            .GetRequiredService<IProfileDefinitionCompiler>()
            .Validate(CreateManifest());
        Assert.True(
            review.IsValid,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var profile = Assert.IsAssignableFrom<ILoadedProfile>(review.Profile);
        provider.GetRequiredService<IProfileCatalogPublisher>().Publish(
            new ProfileCatalogSnapshot([profile], [], DateTimeOffset.UtcNow));

        var result = await provider.GetRequiredService<ICatalogService>().DiscoverAsync(
            new CatalogRequest(profile.Descriptor.Id, workspace.SourcePath, workspace.DatabasePath));

        Assert.Equal(4, result.CandidateCount);
        Assert.Equal(2, result.NoMatchCount);
        Assert.Empty(result.InvalidItems);
        Assert.Equal(2, result.Items.Count);
        var project = Assert.Single(result.Items, item => item.FullPath == projectPath);
        Assert.Equal(new DateTime(2026, 5, 21), project.Values["captured-on"]);
        Assert.Equal("Project", project.Values["name"]);
        var sibling = Assert.Single(result.Items, item => item.FullPath == siblingPath);
        Assert.Equal(new DateTime(2026, 5, 22), sibling.Values["captured-on"]);
        Assert.Equal("Sibling", sibling.Values["name"]);
        Assert.DoesNotContain(result.Items, item => item.FullPath == childPath);

        var discovery = Assert.IsType<global::FindEverything.Application.Indexing.DirectoryDiscoveryReport>(
            result.DiscoveryReport);
        Assert.Equal(
            global::FindEverything.Application.Indexing.DirectoryDiscoveryStatus.Completed,
            discovery.Status);
        Assert.Equal(4, discovery.Progress.Directories);
        Assert.Equal(2, discovery.Progress.PrunedDirectories);
        Assert.Empty(discovery.Errors);
        Assert.Null(result.ScanReport);
        Assert.False(File.Exists(workspace.DatabasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.DatabasePath)));
    }

    private static ProfileManifest CreateManifest() =>
        new()
        {
            ContractVersion = 1,
            Kind = ProfileKind.Declarative,
            Id = "direct-discovery",
            Version = "1.0.0",
            DisplayName = "Direct discovery",
            CandidateKind = ProfileCandidateKind.Directory,
            PathInput = ProfilePathInput.Relative,
            Fields =
            [
                new ProfileFieldManifest
                {
                    FieldId = "captured-on",
                    GroupNames = ["year", "monthDay"],
                    Header = "Date",
                    Order = 10,
                    Required = true,
                    Kind = ProfileFieldValueKind.DateTime,
                    ParseFormat = "yyyyMMdd",
                    DisplayFormat = "yyyy-MM-dd",
                },
                new ProfileFieldManifest
                {
                    FieldId = "name",
                    GroupName = "name",
                    Header = "Name",
                    Order = 20,
                    Required = true,
                    Kind = ProfileFieldValueKind.String,
                },
            ],
            Rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "default",
                    Pattern = @"(?<year>\d{4})[\\/](?<monthDay>\d{4})_(?<name>[^\\/]+)",
                    MatchMode = ProfileRegexMatchMode.Full,
                    TimeoutMilliseconds = 100,
                    StopTraversalWhenCapturedGroups = ["year", "monthDay"],
                },
            ],
        };

    private sealed class NoOpLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
