using FindEverything.Application.Options;
using FindEverything.Desktop.Services;
using System.IO;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class HomeViewModelTests
{
    [Fact]
    public void Empty_workspace_explains_what_is_missing_and_navigation_remains_available()
    {
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot(null, null, null));
        var catalog = new StubProfileCatalog(ProfileCatalogSnapshot.Empty);
        var operation = new StubOperationCoordinator();
        var navigator = new RecordingHomeNavigationService();
        using var viewModel = new HomeViewModel(
            workspace,
            catalog,
            new StubDatabasePathResolver("file.db", "profile.db"),
            operation,
            navigator,
            new TestAppLocalizer());

        Assert.False(viewModel.HasRoot);
        Assert.False(viewModel.HasProfile);
        Assert.False(viewModel.IsReady);
        Assert.Equal("선택 필요", viewModel.RootStateText);
        Assert.Equal("선택 필요", viewModel.ProfileStateText);
        Assert.Contains("프로필을 선택하면", viewModel.ProfileDatabasePath);

        viewModel.OpenFilesCommand.Execute(null);
        Assert.Equal(typeof(FilesPage), navigator.LastPageType);
        viewModel.OpenCatalogCommand.Execute(null);
        Assert.Equal(typeof(CatalogPage), navigator.LastPageType);
        viewModel.OpenProfilesCommand.Execute(null);
        Assert.Equal(typeof(ProfilesPage), navigator.LastPageType);
        viewModel.OpenPlaygroundCommand.Execute(null);
        Assert.Equal(typeof(ProfilePlaygroundPage), navigator.LastPageType);
        viewModel.OpenOutputFormatsCommand.Execute(null);
        Assert.Equal(typeof(OutputFormatsPage), navigator.LastPageType);
    }

    [Fact]
    public void Workspace_profile_database_and_operation_changes_refresh_the_dashboard()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "findeverything-home-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var fileDatabase = Path.Combine(tempDirectory, "files.db");
            var profileDatabase = Path.Combine(tempDirectory, "profile.db");
            var workspace = new StubWorkspaceContext(new WorkspaceSnapshot(
                "projects",
                tempDirectory,
                fileDatabase,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["projects"] = profileDatabase,
                }));
            var catalog = new StubProfileCatalog(new ProfileCatalogSnapshot(
                [new StubLoadedProfile("projects", "Projects")],
                [],
                DateTimeOffset.UtcNow));
            var operation = new StubOperationCoordinator();
            using var viewModel = new HomeViewModel(
                workspace,
                catalog,
                new StubDatabasePathResolver(fileDatabase, profileDatabase),
                operation,
                new RecordingHomeNavigationService(),
                new TestAppLocalizer());

            Assert.True(viewModel.HasRoot);
            Assert.True(viewModel.HasProfile);
            Assert.True(viewModel.IsReady);
            Assert.Equal("Projects", viewModel.ProfileName);
            Assert.Equal("사용자 지정 저장 위치", viewModel.FileDatabaseStateText);
            Assert.Equal("사용자 지정 저장 위치", viewModel.ProfileDatabaseStateText);
            Assert.Equal("구조화 스캔 설정 완료", viewModel.SetupTitle);

            operation.Publish(ApplicationOperationKind.IndexWrite);
            Assert.True(viewModel.IsOperationRunning);
            Assert.Contains("스캔", viewModel.OperationStatus);
            operation.Publish(null);
            Assert.False(viewModel.IsOperationRunning);

            workspace.Publish(workspace.Current with { RootPath = null });
            Assert.False(viewModel.HasRoot);
            Assert.False(viewModel.IsReady);
            Assert.Equal("선택 필요", viewModel.LocationStepState);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class RecordingHomeNavigationService : IHomeNavigationService
    {
        public Type? LastPageType { get; private set; }

        public bool Navigate(Type pageType)
        {
            LastPageType = pageType;
            return true;
        }
    }

    private sealed class StubWorkspaceContext(WorkspaceSnapshot current) : IWorkspaceContext
    {
        public WorkspaceSnapshot Current { get; private set; } = current;

        public event EventHandler<WorkspaceChangedEventArgs>? Changed;

        public Task SaveAsync(
            WorkspaceSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Publish(snapshot);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            Func<WorkspaceSnapshot, WorkspaceSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            Publish(update(Current));
            return Task.CompletedTask;
        }

        public Task SetProfileDatabasePathAsync(
            string profileId,
            string? databasePath,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish(WorkspaceSnapshot snapshot)
        {
            var previous = Current;
            Current = snapshot;
            Changed?.Invoke(this, new WorkspaceChangedEventArgs(previous, snapshot));
        }
    }

    private sealed class StubProfileCatalog(ProfileCatalogSnapshot current) : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; private set; } = current;

        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed;

        public void Publish(ProfileCatalogSnapshot snapshot)
        {
            var previous = Current;
            Current = snapshot;
            Changed?.Invoke(this, new ProfileCatalogChangedEventArgs(previous, snapshot));
        }
    }

    private sealed class StubLoadedProfile(string id, string displayName) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(
            id,
            "1.0.0",
            displayName,
            ProfileCandidateKind.Directory,
            [],
            []);

        public ProfileMapResult Map(ProfilePathCandidate candidate) =>
            ProfileMapResult.NoMatch();

        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(
            string directoryName) =>
            ProfileDirectoryNameExclusionResult.NotExcluded();
    }

    private sealed class StubDatabasePathResolver(
        string fileDatabasePath,
        string profileDatabasePath) : IIndexDatabasePathResolver
    {
        public string ResolveFileSearchDatabasePath() => fileDatabasePath;

        public string ResolveFileSearchDatabasePath(WorkspaceOptions options) =>
            fileDatabasePath;

        public string ResolveFileSearchDatabasePath(WorkspaceSnapshot workspace) =>
            workspace.DatabasePath ?? fileDatabasePath;

        public string ResolveProfileDatabasePath(string profileId) => profileDatabasePath;

        public string ResolveProfileDatabasePath(string profileId, WorkspaceOptions options) =>
            profileDatabasePath;

        public string ResolveProfileDatabasePath(
            string profileId,
            WorkspaceSnapshot workspace) =>
            workspace.ProfileDatabasePaths?
                .FirstOrDefault(pair => string.Equals(
                    pair.Key,
                    profileId,
                    StringComparison.OrdinalIgnoreCase))
                .Value
            ?? profileDatabasePath;
    }

    private sealed class StubOperationCoordinator : IApplicationOperationCoordinator
    {
        public event EventHandler<ApplicationOperationStateChangedEventArgs>? StateChanged;

        public bool IsRunning => CurrentKind is not null;

        public ApplicationOperationKind? CurrentKind { get; private set; }

        public Task RunAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) => operation(cancellationToken);

        public Task RunAsync(
            ApplicationOperationKind kind,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) => operation(cancellationToken);

        public Task ReplaceAsync(
            ApplicationOperationKind replaceableKind,
            ApplicationOperationKind replacementKind,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) => operation(cancellationToken);

        public void Cancel()
        {
        }

        public Task CancelAndWaitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish(ApplicationOperationKind? kind)
        {
            var previous = CurrentKind;
            CurrentKind = kind;
            StateChanged?.Invoke(
                this,
                new ApplicationOperationStateChangedEventArgs(previous, kind));
        }
    }
}
