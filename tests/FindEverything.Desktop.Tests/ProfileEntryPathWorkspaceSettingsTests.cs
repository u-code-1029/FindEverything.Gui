using System.IO;
using System.Text.Json;
using FindEverything.Application.Options;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileEntryPathWorkspaceSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_uses_explicit_local_paths_and_does_not_mutate_the_manifest_or_workspace(bool installed)
    {
        using var fixture = new Fixture();
        var embedded = new List<string> { fixture.Location("embedded") };
        var manifest = Manifest(embedded);
        if (installed) await fixture.MarkInstalledAsync(manifest);
        var local = new[] { fixture.Location("first"), fixture.Location("second") };
        await fixture.Workspace.SetProfileEntryPathsAsync("TARGET", local);

        var exported = await fixture.Service.CreateExportSnapshotAsync(manifest);

        Assert.Equal(local, exported.Workspace!.EntryPaths);
        Assert.NotSame(embedded, exported.Workspace.EntryPaths);
        Assert.Equal([fixture.Location("embedded")], manifest.Workspace!.EntryPaths);
        exported.Workspace.EntryPaths!.Add(fixture.Location("export-only"));
        Assert.Equal(local, fixture.EntryPaths());
        Assert.Equal([fixture.Location("embedded")], embedded);
        Assert.Equal(fixture.Location("global-root"), fixture.Workspace.Current.RootPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_preserves_embedded_paths_when_local_profile_paths_are_unspecified(bool installed)
    {
        using var fixture = new Fixture();
        var manifest = Manifest([fixture.Location("embedded")]);
        if (installed) await fixture.MarkInstalledAsync(manifest);

        var exported = await fixture.Service.CreateExportSnapshotAsync(manifest);

        Assert.Equal(manifest.Workspace!.EntryPaths, exported.Workspace!.EntryPaths);
        Assert.NotSame(manifest.Workspace.EntryPaths, exported.Workspace.EntryPaths);
        Assert.Null(fixture.EntryPaths());
        Assert.DoesNotContain(fixture.Workspace.Current.RootPath!, exported.Workspace.EntryPaths!);
    }

    [Fact]
    public async Task Export_keeps_explicit_empty_paths_and_does_not_invent_a_global_root_fallback()
    {
        using var fixture = new Fixture();
        var manifest = Manifest([fixture.Location("embedded")]);
        await fixture.MarkInstalledAsync(manifest);
        await fixture.Workspace.SetProfileEntryPathsAsync("target", []);

        var exported = await fixture.Service.CreateExportSnapshotAsync(manifest);

        Assert.Empty(exported.Workspace!.EntryPaths!);
        Assert.Empty(fixture.EntryPaths()!);
        Assert.Single(manifest.Workspace!.EntryPaths!);
        await fixture.Workspace.SetProfileEntryPathsAsync("target", null);
        manifest.Workspace.EntryPaths = null;
        Assert.Null((await fixture.Service.CreateExportSnapshotAsync(manifest)).Workspace!.EntryPaths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Install_and_rollback_restore_absent_empty_and_populated_local_paths(int originalKind)
    {
        using var fixture = new Fixture();
        IReadOnlyList<string>? original = originalKind switch
        {
            1 => [],
            2 => [fixture.Location("original")],
            _ => null,
        };
        await fixture.Workspace.SetProfileEntryPathsAsync("target", original);
        await fixture.Workspace.SetProfileEntryPathsAsync("other", [fixture.Location("other-original")]);
        var first = fixture.Location("first");
        var second = fixture.Location("second");
        var incoming = new List<string> { first, second };
        var manifest = Manifest(incoming);

        var installation = await fixture.Service.InstallAsync(manifest);

        Assert.NotNull(installation);
        Assert.Equal([first, second], fixture.EntryPaths());
        incoming.Add(fixture.Location("caller-edit"));
        Assert.Equal([first, second], fixture.EntryPaths());
        var changedRoot = fixture.Location("later-global-root");
        await fixture.Workspace.UpdateAsync(current => current with
        {
            RootPath = changedRoot,
            ProfileEntryPaths = ProfileEntryPathSnapshots.Replace(current.ProfileEntryPaths, "other", [fixture.Location("other-later")]),
        });

        Assert.True(await installation.RollbackAsync());

        Assert.True(ProfileEntryPathSnapshots.Equal(original, fixture.EntryPaths()));
        Assert.Equal([fixture.Location("other-later")], fixture.EntryPaths("other"));
        Assert.Equal(changedRoot, fixture.Workspace.Current.RootPath);
        Assert.Equal("target", fixture.Workspace.Current.SelectedProfileId);
        Assert.Equal(fixture.Location("files.db"), fixture.Workspace.Current.DatabasePath);
        Assert.Equal(fixture.Location("target.db"), fixture.Workspace.Current.ProfileDatabasePaths!["target"]);
    }

    [Fact]
    public async Task Rollback_skips_a_later_user_path_edit_but_preserves_other_workspace_state()
    {
        using var fixture = new Fixture();
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Location("original")]);
        var installation = await fixture.Service.InstallAsync(Manifest([fixture.Location("imported")]));
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Location("later-user-edit")]);
        await fixture.Workspace.SetProfileEntryPathsAsync("other", [fixture.Location("other")]);

        Assert.False(await installation!.RollbackAsync());

        Assert.Equal([fixture.Location("later-user-edit")], fixture.EntryPaths());
        Assert.Equal([fixture.Location("other")], fixture.EntryPaths("other"));
        Assert.Equal(fixture.Location("global-root"), fixture.Workspace.Current.RootPath);
    }

    [Fact]
    public async Task Unspecified_import_paths_never_change_local_paths_on_install_or_rollback()
    {
        using var fixture = new Fixture();
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Location("original")]);
        var writeCount = fixture.Writer.Updates.Count;

        var installation = await fixture.Service.InstallAsync(Manifest(null));

        Assert.Equal([fixture.Location("original")], fixture.EntryPaths());
        Assert.Equal(writeCount, fixture.Writer.Updates.Count);
        Assert.True(await installation!.RollbackAsync());
        Assert.Equal([fixture.Location("original")], fixture.EntryPaths());
        Assert.Equal(writeCount, fixture.Writer.Updates.Count);
    }

    [Fact]
    public async Task Explicit_empty_import_paths_are_persisted_and_rollback_restores_the_previous_list()
    {
        using var fixture = new Fixture();
        var original = new[] { fixture.Location("original") };
        await fixture.Workspace.SetProfileEntryPathsAsync("target", original);

        var installation = await fixture.Service.InstallAsync(Manifest([]));

        Assert.Empty(fixture.EntryPaths()!);
        Assert.Empty(fixture.Writer.Updates.Last().Workspace!.ProfileEntryPaths!["target"]);
        Assert.Equal(fixture.Location("global-root"), fixture.Workspace.Current.RootPath);
        Assert.True(await installation!.RollbackAsync());
        Assert.Equal(original, fixture.EntryPaths());
    }

    [Fact]
    public async Task A_later_store_write_failure_rolls_back_installed_paths()
    {
        using var fixture = new Fixture();
        var original = new[] { fixture.Location("original") };
        await fixture.Workspace.SetProfileEntryPathsAsync("target", original);
        Directory.CreateDirectory(fixture.Paths.GridLayoutFile);

        var failure = await Record.ExceptionAsync(() => fixture.Service.InstallAsync(Manifest([fixture.Location("imported")])));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(original, fixture.EntryPaths());
        Assert.Equal(original, fixture.Writer.Updates.Last().Workspace!.ProfileEntryPaths!["target"]);
        Assert.Equal(fixture.Location("global-root"), fixture.Workspace.Current.RootPath);
    }

    [Fact]
    public async Task Cancelled_install_does_not_change_profile_entry_paths()
    {
        using var fixture = new Fixture();
        await fixture.Workspace.SetProfileEntryPathsAsync("target", [fixture.Location("original")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.InstallAsync(Manifest([fixture.Location("imported")]), cancellation.Token));

        Assert.Equal([fixture.Location("original")], fixture.EntryPaths());
    }

    private static ProfileManifest Manifest(List<string>? entryPaths) => new()
    {
        ContractVersion = ProfileContract.CurrentMajor,
        Kind = ProfileKind.Declarative,
        Id = "target",
        Version = "1.0.0",
        DisplayName = "Entry paths",
        Fields = [new() { FieldId = "name", GroupName = "name", Header = "Name", Kind = ProfileFieldValueKind.String }],
        Rules = [new() { Id = "rule", Pattern = "(?<name>.+)", MatchMode = ProfileRegexMatchMode.Full }],
        Workspace = new() { EntryPaths = entryPaths },
    };

    private sealed class Fixture : IDisposable
    {
        private readonly GridLayoutStore _layouts;

        public Fixture()
        {
            var directory = Path.Combine(Path.GetTempPath(), "fe-entry-path-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Paths = new(directory, Path.Combine(directory, "settings.json"));
            Writer = new RecordingSettingsWriter();
            Workspace = new(new SettingsState(new()
            {
                SelectedProfileId = "target",
                RootPath = Location("global-root"),
                FileSearchDatabasePath = Location("files.db"),
                ProfileDatabasePaths = new() { ["target"] = Location("target.db") },
            }), Writer);
            _layouts = new(Paths, NullLogger<GridLayoutStore>.Instance);
            var presentation = new ProfilePresentationStore(Paths, _layouts, NullLogger<ProfilePresentationStore>.Instance);
            var formats = new SelectionOutputFormatStore(Paths, NullLogger<SelectionOutputFormatStore>.Instance);
            var preferences = new ProfileOutputPreferenceStore(Paths, NullLogger<ProfileOutputPreferenceStore>.Instance);
            Service = new(Paths, formats, presentation, _layouts, preferences, new SelectionOutputFormatter(),
                workspaceContext: Workspace);
        }

        public AppPaths Paths { get; }
        public RecordingSettingsWriter Writer { get; }
        public WorkspaceContext Workspace { get; }
        public ProfileWorkspaceSettingsService Service { get; }

        public string Location(string name) => Path.Combine(Paths.LocalDataDirectory, name);

        public IReadOnlyList<string>? EntryPaths(string profileId = "target") =>
            ProfileEntryPathSnapshots.Get(Workspace.Current.ProfileEntryPaths, profileId);

        public async Task MarkInstalledAsync(ProfileManifest manifest)
        {
            var directory = Path.Combine(Paths.UserProfilesDirectory, manifest.Id!);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "profile.json"), JsonSerializer.Serialize(manifest));
        }

        public void Dispose()
        {
            _layouts.StopAsync(default).GetAwaiter().GetResult();
            Workspace.Dispose();
            if (Directory.Exists(Paths.LocalDataDirectory)) Directory.Delete(Paths.LocalDataDirectory, recursive: true);
        }
    }

    private sealed class SettingsState(WorkspaceOptions options) : IValidatedSettingsState<WorkspaceOptions>
    {
        public WorkspaceOptions Current { get; } = options;
    }

    private sealed class RecordingSettingsWriter : IUserSettingsWriter
    {
        public List<UserSettingsUpdate> Updates { get; } = [];

        public Task SaveAsync(UserSettingsUpdate update, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }
}
