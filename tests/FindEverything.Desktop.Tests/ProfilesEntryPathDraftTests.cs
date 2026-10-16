using System.IO;
using System.Reflection;
using FindEverything.Application.Options;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using FindEverything.Profile.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfilesEntryPathDraftTests
{
    [Fact]
    public async Task Saving_the_editor_persists_all_locations_for_its_profile_and_in_the_portable_snapshot()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.AddEntryPathCommand.Execute(null);
        vm.DraftEntryPaths[0].Path = fixture.Path("first");
        vm.AddEntryPathCommand.Execute(null);
        vm.DraftEntryPaths[1].Path = fixture.Path("second");
        Assert.True(vm.HasUnsavedChanges);

        Assert.True(await vm.SaveChangesAsync());

        var paths = new[] { fixture.Path("first"), fixture.Path("second") };
        Assert.Equal(paths, ProfileEntryPathSnapshots.Get(fixture.Workspace.Current.ProfileEntryPaths, vm.DraftId));
        Assert.Equal(paths, fixture.Authoring.Saved!.Workspace!.EntryPaths);
        Assert.Equal(fixture.Path("files"), fixture.Workspace.Current.RootPath);
        Assert.False(vm.HasUnsavedChanges);
        vm.RemoveEntryPathCommand.Execute(vm.DraftEntryPaths[0]);
        vm.RemoveEntryPathCommand.Execute(vm.DraftEntryPaths[0]);
        Assert.True(await vm.SaveChangesAsync());
        Assert.Empty(ProfileEntryPathSnapshots.Get(fixture.Workspace.Current.ProfileEntryPaths, vm.DraftId)!);
        Assert.Empty(fixture.Authoring.Saved!.Workspace!.EntryPaths!);
    }

    [Fact]
    public async Task Loaded_paths_prefer_local_settings_refresh_clean_drafts_and_preserve_dirty_edits()
    {
        using var fixture = new Fixture();
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Path("local")]);
        fixture.Authoring.Loaded = Manifest([fixture.Path("embedded")]);
        fixture.ViewModel.SelectedEditableProfile = new("target", "Target", "unused");
        await fixture.ViewModel.LoadDraftCommand.ExecuteAsync(null);
        Assert.Equal(fixture.Path("local"), Assert.Single(fixture.ViewModel.DraftEntryPaths).Path);
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Path("external")]);
        Assert.Equal(fixture.Path("external"), Assert.Single(fixture.ViewModel.DraftEntryPaths).Path);
        fixture.ViewModel.DraftEntryPaths[0].Path = fixture.Path("unsaved");
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Path("later")]);
        Assert.Equal(fixture.Path("unsaved"), Assert.Single(fixture.ViewModel.DraftEntryPaths).Path);
        Assert.Equal([fixture.Path("unsaved")], Build(fixture.ViewModel).Workspace!.EntryPaths);
    }

    [Fact]
    public async Task Saving_an_embedded_location_list_installs_it_when_no_local_settings_exist()
    {
        using var fixture = new Fixture();
        fixture.Authoring.Loaded = Manifest([fixture.Path("embedded")]);
        fixture.ViewModel.SelectedEditableProfile = new("target", "Target", "unused");
        await fixture.ViewModel.LoadDraftCommand.ExecuteAsync(null);
        Assert.True(await fixture.ViewModel.SaveChangesAsync());
        Assert.Equal([fixture.Path("embedded")], ProfileEntryPathSnapshots.Get(fixture.Workspace.Current.ProfileEntryPaths, "target"));
    }

    [Fact]
    public async Task Folder_browse_uses_the_async_picker_and_entry_rows_are_bounded()
    {
        using var fixture = new Fixture();
        fixture.Picker.Selection = fixture.Path("picked");
        await fixture.ViewModel.BrowseEntryPathCommand.ExecuteAsync(null);
        Assert.Equal(fixture.Picker.Selection, Assert.Single(fixture.ViewModel.DraftEntryPaths).Path);
        while (fixture.ViewModel.DraftEntryPaths.Count < ProfileWorkspaceManifestValidator.MaximumEntryPaths)
            fixture.ViewModel.AddEntryPathCommand.Execute(null);
        Assert.False(fixture.ViewModel.AddEntryPathCommand.CanExecute(null));
        fixture.ViewModel.AddEntryPathCommand.Execute(null);
        Assert.Equal(ProfileWorkspaceManifestValidator.MaximumEntryPaths, fixture.ViewModel.DraftEntryPaths.Count);
    }

    private static ProfileManifest Build(ProfilesViewModel vm) => (ProfileManifest)typeof(ProfilesViewModel)
        .GetMethod("BuildManifest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
    private static ProfileManifest Manifest(List<string> paths) => new()
    {
        Id = "target", DisplayName = "Target", Version = "1", ContractVersion = ProfileContract.CurrentMajor,
        Kind = ProfileKind.Declarative, Workspace = new() { EntryPaths = paths },
        Fields = [new() { FieldId = "name", GroupName = "name", Header = "Name" }],
        Rules = [new() { Id = "default", Pattern = "(?<name>.+)" }],
    };
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "profile-draft-entry-" + Guid.NewGuid().ToString("N"));
        public WorkspaceContext Workspace { get; }
        public Authoring Authoring { get; } = new();
        public Picker Picker { get; } = new();
        public ProfilesViewModel ViewModel { get; }
        public Fixture()
        {
            Workspace = new(new Settings(new() { RootPath = Path("files") }), new Writer());
            ViewModel = new(new Catalog(), Authoring, new ProfilePathTemplateCompiler(), new AbsoluteProfilePathCanonicalizer(),
                null!, new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance),
                new SettingsViewModelTests.NoopSnackbar(), null!, null!, new TestAppLocalizer(), NullLogger<ProfilesViewModel>.Instance,
                workspaceContext: Workspace, pickerService: Picker);
        }
        public string Path(string name) => System.IO.Path.Combine(_root, name);
        public void Dispose() { ViewModel.Dispose(); Workspace.Dispose(); }
    }
    private sealed class Settings(WorkspaceOptions current) : IValidatedSettingsState<WorkspaceOptions> { public WorkspaceOptions Current => current; }
    private sealed class Writer : IUserSettingsWriter { public Task SaveAsync(UserSettingsUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class Catalog : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current => ProfileCatalogSnapshot.Empty;
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }
    private sealed class LoadedProfile(string id) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(id, "1", id, ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }
    private sealed class Authoring : IProfileAuthoringService
    {
        public ProfileManifest? Loaded { get; set; }
        public ProfileManifest? Saved { get; private set; }
        public string UserProfilesDirectory => System.IO.Path.GetTempPath();
        public Task<IReadOnlyList<EditableProfileSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EditableProfileSummary>>([]);
        public Task<ProfileManifest?> LoadAsync(EditableProfileSummary profile, CancellationToken cancellationToken = default) => Task.FromResult(Loaded);
        public ProfileDefinitionReview Validate(ProfileManifest manifest) => new(new LoadedProfile(manifest.Id!), ProfileWorkspaceManifestValidator.Validate(manifest));
        public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath) => throw new NotSupportedException();
        public Task<ProfileSaveResult> SaveAndApplyAsync(ProfileManifest manifest, string? originalProfileId, CancellationToken cancellationToken = default)
        {
            Saved = manifest;
            var review = Validate(manifest);
            return Task.FromResult(new ProfileSaveResult(true, review.IsValid, review,
                new([review.Profile!], [], DateTimeOffset.UtcNow)));
        }
    }
    private sealed class Picker : IDesktopPickerService
    {
        public string? Selection { get; set; }
        public string? PickRootDirectory(string? initialDirectory) => throw new InvalidOperationException("Use async picker.");
        public Task<string?> PickRootDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken = default) => Task.FromResult(Selection);
        public string? PickDatabasePath(string? currentPath) => null;
        public string? PickScanLogPath(string suggestedFileName) => null;
        public CatalogSelectionExportDestination? PickCatalogExportPath(string suggestedFileName) => null;
    }
}
