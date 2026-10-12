using System.IO;
using System.Reflection;
using FindEverything.Application.Catalog;
using FindEverything.Application.Options;
using FindEverything.Application.Results;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class CatalogOperationSafetyTests
{
    [Fact]
    public void Switching_profiles_resets_profile_specific_sort_and_keeps_bounded_text_previews()
    {
        using var vm = CreateViewModel(new WaitingCoordinator(), projection: true);
        var definition = typeof(CatalogViewModel).GetField("_queryDefinition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Represents the query produced by sorting profile A's client column.
        definition.SetValue(vm, new CatalogProjectionQuery(SortFieldId: "client", Descending: true)
        { TextPreviewCharacters = 2048 });
        vm.FilterText = "old profile filter";
        vm.ShowOriginalValues = true;

        vm.SelectedProfile = vm.Profiles.Single(profile => profile.Id == "b");

        var reset = Assert.IsType<CatalogProjectionQuery>(definition.GetValue(vm));
        Assert.Null(reset.SortFieldId);
        Assert.False(reset.Descending);
        Assert.Equal(2048, reset.TextPreviewCharacters);
        Assert.Empty(vm.FilterText);
        Assert.False(vm.ShowOriginalValues);
        Assert.DoesNotContain(vm.Fields, field => field.FieldId == "client");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancelling_or_disposing_during_load_replacement_never_starts_a_scan(bool projection, bool dispose)
    {
        var coordinator = new WaitingCoordinator();
        using var vm = CreateViewModel(coordinator, projection);
        vm.IsBusy = true;
        vm.IsLoadingExistingIndex = true;

        var scan = vm.ScanCommand.ExecuteAsync(null);
        await coordinator.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));
        if (dispose) vm.Dispose();
        else vm.CancelCommand.Execute(null);
        await scan.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(coordinator.WaitToken.IsCancellationRequested);
        Assert.Equal(0, coordinator.StartedOperations);
        // Releasing the old reader after cancellation must not resume the scan.
        coordinator.ReleaseWait.TrySetResult();
        vm.IsLoadingExistingIndex = false;
        vm.IsBusy = false;
        Assert.Equal(!dispose, vm.ScanCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_also_stops_waiting_for_the_previous_projection_ui_cleanup()
    {
        var coordinator = new WaitingCoordinator();
        using var vm = CreateViewModel(coordinator, projection: true);
        vm.IsBusy = true;
        vm.IsLoadingExistingIndex = true;
        var scan = vm.ScanCommand.ExecuteAsync(null);
        await coordinator.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.ReleaseWait.TrySetResult();
        // The read slot is released but its UI-side finally has not cleared IsBusy.
        vm.CancelCommand.Execute(null);
        await scan.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, coordinator.StartedOperations);
        vm.IsLoadingExistingIndex = false;
        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    private static CatalogViewModel CreateViewModel(WaitingCoordinator coordinator, bool projection) => new(
        null!, new Catalog(), new Workspace(), new DatabaseResolver(), null!, null!, coordinator,
        null!, null!, new Formats(), null!, null!, null!, new TestAppLocalizer(),
        NullLogger<CatalogViewModel>.Instance,
        projectionStore: projection ? new UnusedProjectionStore() : null);

    private sealed class Profile(string id, string fieldId) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(id, "1.0", id,
            ProfileCandidateKind.Directory,
            [new(fieldId, fieldId, fieldId, 0, false, ProfileFieldValueKind.String, true, null, null)], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }

    private sealed class Catalog : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; } = new(
            [new Profile("a", "client"), new Profile("b", "department")], [], DateTimeOffset.UtcNow);
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }

    private sealed class Workspace : IWorkspaceContext
    {
        public WorkspaceSnapshot Current { get; } = new("a", Path.GetTempPath(), null);
        public event EventHandler<WorkspaceChangedEventArgs>? Changed { add { } remove { } }
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Func<WorkspaceSnapshot, WorkspaceSnapshot> update, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetProfileDatabasePathAsync(string profileId, string? databasePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DatabaseResolver : IIndexDatabasePathResolver
    {
        private static string Database => Path.Combine(Path.GetTempPath(), "catalog-operation-safety.db");
        public string ResolveFileSearchDatabasePath() => Database;
        public string ResolveFileSearchDatabasePath(WorkspaceOptions options) => Database;
        public string ResolveFileSearchDatabasePath(WorkspaceSnapshot workspace) => Database;
        public string ResolveProfileDatabasePath(string profileId) => Database;
        public string ResolveProfileDatabasePath(string profileId, WorkspaceOptions options) => Database;
        public string ResolveProfileDatabasePath(string profileId, WorkspaceSnapshot workspace) => Database;
    }

    private sealed class Formats : ISelectionOutputFormatStore
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<SelectionOutputFormatDefinition> GetAll() => [];
        public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId) => [];
        public void Save(SelectionOutputFormatDefinition definition) => throw new NotSupportedException();
        public bool Delete(string id) => false;
    }

    private sealed class WaitingCoordinator : IApplicationOperationCoordinator
    {
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken WaitToken { get; private set; }
        public int StartedOperations { get; private set; }
        public bool IsRunning => !ReleaseWait.Task.IsCompleted;
        public ApplicationOperationKind? CurrentKind => IsRunning ? ApplicationOperationKind.IndexLoad : null;
        public event EventHandler<ApplicationOperationStateChangedEventArgs>? StateChanged { add { } remove { } }
        public void Cancel() { }
        public Task CancelAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            WaitToken = cancellationToken;
            WaitStarted.TrySetResult();
            return ReleaseWait.Task.WaitAsync(cancellationToken);
        }
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            RunAsync(ApplicationOperationKind.Exclusive, operation, cancellationToken);
        public Task RunAsync(ApplicationOperationKind kind, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        { StartedOperations++; return operation(cancellationToken); }
        public Task ReplaceAsync(ApplicationOperationKind replaceableKind, ApplicationOperationKind replacementKind,
            Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            RunAsync(replacementKind, operation, cancellationToken);
    }

    private sealed class UnusedProjectionStore : ICatalogProjectionStore
    {
        public Task<CatalogProjectionSnapshot?> FindAsync(CatalogProjectionScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ICatalogProjectionWriter Begin(CatalogProjectionScope scope, IReadOnlyList<ProfileFieldDescriptor> fields) => throw new NotSupportedException();
        public IResultQuerySession<CatalogProjectionRow> OpenQuery(CatalogProjectionSnapshot snapshot, CatalogProjectionQuery query) => throw new NotSupportedException();
        public IResultQuerySession<CatalogProjectionRow> OpenBuildingQuery(ICatalogProjectionWriter writer, CatalogProjectionScope scope,
            IReadOnlyList<ProfileFieldDescriptor> fields, CatalogProjectionQuery query) => throw new NotSupportedException();
        public Task<CatalogProjectionSnapshot> RefreshColumnAsync(CatalogProjectionSnapshot snapshot, string fieldId,
            Func<string, CancellationToken, Task<TextColumnReadResult>> readValue, IProgress<TextColumnRefreshProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
