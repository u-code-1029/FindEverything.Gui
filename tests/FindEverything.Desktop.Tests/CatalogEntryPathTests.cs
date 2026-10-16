using System.IO;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task Switching_profiles_restores_only_its_paths_and_clears_pending_path_input() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var vm = fixture.ViewModel;
        var originalProfile = vm.SelectedProfile;
        vm.NewEntryPath = Path.Combine(Path.GetTempPath(), "sample-extra");
        await vm.AddEntryPathCommand.ExecuteAsync(null);
        var originalPaths = vm.EntryPaths.ToArray();
        vm.NewEntryPath = "unfinished path for sample";
        vm.SelectedProfile = new ProfileChoiceViewModel(new OtherEntryProfile());
        Assert.Empty(vm.EntryPaths);
        Assert.Empty(vm.NewEntryPath);
        var otherPath = Path.Combine(Path.GetTempPath(), "other-profile");
        vm.NewEntryPath = otherPath;
        await vm.AddEntryPathCommand.ExecuteAsync(null);
        vm.SelectedProfile = originalProfile;
        Assert.Equal(originalPaths, vm.EntryPaths);
        Assert.Equal(new[] { otherPath }, ProfileEntryPathSnapshots.Get(fixture.Workspace.Current.ProfileEntryPaths, "other"));
    });

    private sealed class OtherEntryProfile : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new("other", "1.0", "Other",
            ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }

    [Fact]
    public Task Entry_paths_add_remove_deduplicate_and_persist_per_profile() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var vm = fixture.ViewModel;
        var initial = Assert.Single(vm.EntryPaths);
        var second = Path.Combine(Path.GetTempPath(), "entry-two");
        vm.NewEntryPath = second;
        await vm.AddEntryPathCommand.ExecuteAsync(null);
        Assert.Equal(new[] { initial, second }, vm.EntryPaths);
        Assert.Equal(vm.EntryPaths, ProfileEntryPathSnapshots.Get(fixture.Workspace.Current.ProfileEntryPaths, "sample"));
        vm.NewEntryPath = second + Path.DirectorySeparatorChar;
        await vm.AddEntryPathCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.EntryPaths.Count);
        await vm.RemoveEntryPathCommand.ExecuteAsync(initial);
        Assert.Equal(second, Assert.Single(vm.EntryPaths));
        Assert.Equal(second, vm.RootPath);
        await vm.RemoveEntryPathCommand.ExecuteAsync(second);
        Assert.Empty(vm.EntryPaths);
        Assert.Null(vm.RootPath);
        Assert.Empty(ProfileEntryPathSnapshots.Get(fixture.Workspace.Current.ProfileEntryPaths, "sample")!);
    });

    [Fact]
    public Task Relative_entry_path_is_rejected_without_mutating_saved_locations() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var original = fixture.ViewModel.EntryPaths.ToArray();
        fixture.ViewModel.NewEntryPath = "relative-folder";
        await fixture.ViewModel.AddEntryPathCommand.ExecuteAsync(null);
        Assert.Equal(original, fixture.ViewModel.EntryPaths);
        Assert.Null(fixture.Workspace.Current.ProfileEntryPaths);
    });

    [Fact]
    public Task Multi_root_scan_passes_all_paths_and_publishes_one_complete_scope() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: false);
        var folder = Directory.CreateTempSubdirectory("catalog-entries-");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(folder.FullName, "a")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(folder.FullName, "b")).FullName;
            fixture.ViewModel.RootPath = second;
            fixture.ViewModel.NewEntryPath = first;
            await fixture.ViewModel.AddEntryPathCommand.ExecuteAsync(null);
            CatalogRequest? captured = null;
            fixture.Service.ScanOverride = (request, _) =>
            {
                captured = request;
                foreach (var path in request.EntryPaths)
                    request.ResultWriter!.Append(new(path, ".", "rule", new object(),
                        new Dictionary<string, object?> { ["client"] = path }, false));
                return Task.FromResult(new CatalogResult(new SampleProfile().Descriptor, [], [], 2, 0, false, null)
                { MatchedCount = 2 });
            };
            await fixture.ViewModel.ScanCommand.ExecuteAsync(null);
            Assert.NotNull(captured);
            Assert.Equal(new[] { first, second }, captured.EntryPaths);
            Assert.Equal(1, fixture.Service.ScanCalls);
            Assert.Equal(1, fixture.Store.BeginCalls);
            Assert.True(fixture.Store.LastSnapshot!.Scope.IsMultiRoot);
            Assert.Equal(captured.EntryPaths, fixture.Store.LastSnapshot.Scope.EffectiveRootPaths);
        }
        finally { folder.Delete(recursive: true); }
    });

    [Fact]
    public Task Multi_root_load_uses_completed_projection_without_touching_offline_entry_drives() => OnStaAsync(async () =>
    {
        using var fixture = new Fixture(cacheExists: true);
        fixture.ViewModel.RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "offline-a");
        fixture.ViewModel.NewEntryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "offline-b");
        await fixture.ViewModel.AddEntryPathCommand.ExecuteAsync(null);
        await fixture.ViewModel.LoadCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Store.FindLastPublishedCalls);
        Assert.Equal(0, fixture.Service.LoadCalls);
        Assert.Equal(0, fixture.Service.ScanCalls);
        Assert.True(fixture.ViewModel.HasLoadedItems);
        Assert.True(fixture.Store.LastSnapshot!.Scope.IsMultiRoot);
    });
}
