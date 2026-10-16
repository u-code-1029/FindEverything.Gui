using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class HomeViewModelTests
{
    [Fact]
    public void Readiness_uses_the_selected_profiles_entry_list_and_updates_without_a_global_root()
    {
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", null, null,
            ProfileEntryPaths: EntryMap(("a", [@"C:\Projects", @"\\server\Archive"]), ("b", []))));
        using var vm = CreateMultiRootHome(workspace);
        Assert.True(vm.IsReady);
        Assert.True(vm.HasRoot);
        Assert.Equal("시작 경로 2개", vm.RootPathText);
        Assert.Equal(vm.RootPathText, vm.CurrentLocation);
        Assert.False(vm.NeedsLocation);

        workspace.Publish(workspace.Current with { SelectedProfileId = "b" });
        Assert.True(vm.HasProfile);
        Assert.False(vm.HasRoot);
        Assert.False(vm.IsReady);
        Assert.True(vm.NeedsLocation);
        Assert.Equal("선택 필요", vm.LocationStepState);

        workspace.Publish(workspace.Current with { SelectedProfileId = "a" });
        Assert.True(vm.IsReady);
        Assert.Equal("시작 경로 2개", vm.RootPathText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_explicit_empty_or_missing_profile_list_does_not_resurrect_another_profiles_global_root(bool explicitEmpty)
    {
        var paths = EntryMap(("b", [@"D:\OtherProfile"]));
        if (explicitEmpty) paths["a"] = [];
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", @"D:\OtherProfile", null,
            ProfileEntryPaths: paths));
        using var vm = CreateMultiRootHome(workspace);
        Assert.True(vm.HasProfile);
        Assert.False(vm.HasRoot);
        Assert.False(vm.IsReady);
    }

    [Fact]
    public void Legacy_global_root_is_used_only_before_any_profile_entry_lists_exist()
    {
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", @"C:\Legacy", null));
        using var vm = CreateMultiRootHome(workspace);
        Assert.True(vm.IsReady);
        Assert.Equal(@"C:\Legacy", vm.RootPathText);
        workspace.Publish(workspace.Current with { ProfileEntryPaths = EntryMap(("a", [@"E:\Configured"])) });
        Assert.Equal(@"E:\Configured", vm.RootPathText);
    }

    [Fact]
    public async Task Choosing_a_location_appends_to_the_current_profile_preserving_all_other_roots_and_settings()
    {
        var paths = EntryMap(("a", [@"C:\Projects", @"D:\Archive"]), ("b", [@"E:\Other"]));
        var databases = new Dictionary<string, string> { ["a"] = "a.db", ["b"] = "b.db" };
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", @"C:\Projects", "files.db", databases, paths));
        var picker = new ControlledHomePicker { Result = @"\\server\Team" };
        using var vm = CreateMultiRootHome(workspace, picker);

        await vm.ChooseLocationCommand.ExecuteAsync(null);

        Assert.Equal([@"C:\Projects", @"D:\Archive", @"\\server\Team"], GetEntries(workspace, "a"));
        Assert.Equal([@"E:\Other"], GetEntries(workspace, "b"));
        Assert.Equal(@"\\server\Team", workspace.Current.RootPath); // Deliberate legacy file-search mirror.
        Assert.Equal("files.db", workspace.Current.DatabasePath);
        Assert.Same(databases, workspace.Current.ProfileDatabasePaths);
        Assert.Equal("a", workspace.Current.SelectedProfileId);
        Assert.Equal(@"C:\Projects", picker.InitialDirectory);
        Assert.Equal("시작 경로 3개", vm.RootPathText);
        Assert.Equal([@"C:\Projects", @"D:\Archive"], paths["a"]); // Previous snapshot is immutable to the update.
    }

    [Fact]
    public async Task Choosing_an_existing_root_is_case_insensitively_deduplicated_without_reordering()
    {
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", @"C:\Projects", null,
            ProfileEntryPaths: EntryMap(("a", [@"C:\Projects", @"D:\Archive"]), ("b", [@"E:\Other"]))));
        using var vm = CreateMultiRootHome(workspace, new ControlledHomePicker { Result = @"d:\ARCHIVE" });
        await vm.ChooseLocationCommand.ExecuteAsync(null);
        Assert.Equal([@"C:\Projects", @"D:\Archive"], GetEntries(workspace, "a"));
        Assert.Equal([@"E:\Other"], GetEntries(workspace, "b"));
    }

    [Fact]
    public async Task A_cancelled_picker_preserves_the_complete_workspace()
    {
        var original = new WorkspaceSnapshot("a", @"C:\Projects", "files.db",
            ProfileEntryPaths: EntryMap(("a", [@"C:\Projects", @"D:\Archive"])));
        var workspace = new StubWorkspaceContext(original);
        using var vm = CreateMultiRootHome(workspace, new ControlledHomePicker());
        await vm.ChooseLocationCommand.ExecuteAsync(null);
        Assert.Same(original, workspace.Current);
    }

    [Fact]
    public async Task A_profile_switch_while_the_picker_is_open_never_writes_into_either_profile()
    {
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", @"C:\Projects", null,
            ProfileEntryPaths: EntryMap(("a", [@"C:\Projects"]), ("b", [@"D:\Other"]))));
        var picker = new ControlledHomePicker { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = CreateMultiRootHome(workspace, picker);
        var choosing = vm.ChooseLocationCommand.ExecuteAsync(null);
        workspace.Publish(workspace.Current with { SelectedProfileId = "b" });
        var switched = workspace.Current;
        picker.Completion.SetResult(@"E:\MustNotBeAdded");
        await choosing;
        Assert.Same(switched, workspace.Current);
        Assert.Equal([@"C:\Projects"], GetEntries(workspace, "a"));
        Assert.Equal([@"D:\Other"], GetEntries(workspace, "b"));
    }

    [Fact]
    public async Task Picker_completion_merges_with_the_latest_same_profile_lists_not_the_opening_snapshot()
    {
        var workspace = new StubWorkspaceContext(new WorkspaceSnapshot("a", @"C:\Projects", null,
            ProfileEntryPaths: EntryMap(("a", [@"C:\Projects"]), ("b", [@"D:\Other"]))));
        var picker = new ControlledHomePicker { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = CreateMultiRootHome(workspace, picker);
        var choosing = vm.ChooseLocationCommand.ExecuteAsync(null);
        workspace.Publish(workspace.Current with
        {
            DatabasePath = "new-files.db",
            ProfileEntryPaths = EntryMap(("a", [@"C:\Projects", @"E:\AddedElsewhere"]),
                ("b", [@"D:\Other", @"F:\OtherAddedElsewhere"])),
        });
        picker.Completion.SetResult(@"G:\Picked");
        await choosing;
        Assert.Equal([@"C:\Projects", @"E:\AddedElsewhere", @"G:\Picked"], GetEntries(workspace, "a"));
        Assert.Equal([@"D:\Other", @"F:\OtherAddedElsewhere"], GetEntries(workspace, "b"));
        Assert.Equal("new-files.db", workspace.Current.DatabasePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_picker_result_is_ignored_after_disposal_or_an_operation_starts(bool dispose)
    {
        var original = new WorkspaceSnapshot("a", @"C:\Projects", null,
            ProfileEntryPaths: EntryMap(("a", [@"C:\Projects"])));
        var workspace = new StubWorkspaceContext(original);
        var operation = new StubOperationCoordinator();
        var picker = new ControlledHomePicker { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = CreateMultiRootHome(workspace, picker, operation);
        var choosing = vm.ChooseLocationCommand.ExecuteAsync(null);
        if (dispose) vm.Dispose(); else operation.Publish(ApplicationOperationKind.IndexWrite);
        picker.Completion.SetResult(@"D:\MustNotBeAdded");
        await choosing;
        Assert.Same(original, workspace.Current);
    }

    private static HomeViewModel CreateMultiRootHome(StubWorkspaceContext workspace,
        IDesktopPickerService? picker = null, StubOperationCoordinator? operation = null) => new(workspace,
        new StubProfileCatalog(new ProfileCatalogSnapshot(
            [new StubLoadedProfile("a", "Profile A"), new StubLoadedProfile("b", "Profile B")], [], DateTimeOffset.UtcNow)),
        new StubDatabasePathResolver("files.db", "profile.db"), operation ?? new StubOperationCoordinator(),
        new RecordingHomeNavigationService(), new TestAppLocalizer(), picker: picker);

    private static Dictionary<string, IReadOnlyList<string>> EntryMap(
        params (string Profile, string[] Paths)[] values) => values.ToDictionary(
            item => item.Profile, item => (IReadOnlyList<string>)item.Paths, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> GetEntries(StubWorkspaceContext workspace, string profileId) =>
        Assert.IsAssignableFrom<IReadOnlyList<string>>(ProfileEntryPathSnapshots.Get(workspace.Current.ProfileEntryPaths, profileId));

    private sealed class ControlledHomePicker : IDesktopPickerService
    {
        public string? Result { get; init; }
        public TaskCompletionSource<string?>? Completion { get; init; }
        public string? InitialDirectory { get; private set; }
        public Task<string?> PickRootDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken = default)
        {
            InitialDirectory = initialDirectory;
            return Completion?.Task ?? Task.FromResult(Result);
        }
        public string? PickRootDirectory(string? initialDirectory) => throw new InvalidOperationException("Use the asynchronous picker.");
        public string? PickDatabasePath(string? currentPath) => throw new NotSupportedException();
        public string? PickScanLogPath(string suggestedFileName) => throw new NotSupportedException();
        public CatalogSelectionExportDestination? PickCatalogExportPath(string suggestedFileName) => throw new NotSupportedException();
    }
}
