using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Fact]
    public Task Catalog_profile_picker_cancellation_preserves_the_current_profile_and_output_format() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        var selected = fixture.ViewModel.SelectedProfile;
        var format = fixture.ViewModel.SelectedOutputFormat;
        fixture.Picker.ProfilePath = null;

        Assert.True(fixture.ViewModel.ImportProfileCommand.CanExecute(null));
        await fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Picker.ProfilePickerCalls);
        Assert.Equal(0, fixture.Picker.WorkspacePickerCalls);
        Assert.Equal(0, fixture.Transfers.InspectionCalls);
        Assert.Same(selected, fixture.ViewModel.SelectedProfile);
        Assert.Same(format, fixture.ViewModel.SelectedOutputFormat);
        Assert.Empty(fixture.Preferences.Saves);
    });

    [Theory]
    [InlineData("dataset.fepkg")]
    [InlineData("profile.json.exe")]
    public Task Catalog_profile_import_rejects_non_json_before_inspecting_or_installing(string path) => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        var selected = fixture.ViewModel.SelectedProfile;
        fixture.Picker.ProfilePath = path;

        await fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Picker.ProfilePickerCalls);
        Assert.Equal(0, fixture.Picker.WorkspacePickerCalls);
        Assert.Equal(0, fixture.Transfers.InspectionCalls);
        Assert.Equal(0, fixture.Transfers.ManifestImportCalls);
        Assert.Same(selected, fixture.ViewModel.SelectedProfile);
        Assert.Equal(InfoBarSeverity.Error, fixture.ViewModel.StatusSeverity);
        Assert.Contains("JSON", fixture.ViewModel.StatusMessage);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Catalog_profile_import_failure_preserves_the_previous_profile(bool inspectionFails) => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        var selected = fixture.ViewModel.SelectedProfile;
        var format = fixture.ViewModel.SelectedOutputFormat;
        if (inspectionFails) fixture.Transfers.InspectionError = new InvalidDataException("Invalid profile document.");
        else fixture.Transfers.ImportError = new IOException("Profile installation failed.");

        await fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);

        Assert.Same(selected, fixture.ViewModel.SelectedProfile);
        Assert.Same(format, fixture.ViewModel.SelectedOutputFormat);
        Assert.Equal(inspectionFails ? 0 : 1, fixture.Transfers.ManifestImportCalls);
        Assert.Contains(fixture.Notifications.Messages, message =>
            message.Contains(inspectionFails ? "Invalid profile" : "installation failed", StringComparison.Ordinal));
        Assert.False(fixture.TransferViewModel.IsBusy);
        Assert.Empty(fixture.Preferences.Saves);
    });

    [Fact]
    public Task Catalog_profile_import_respects_draft_and_conflict_cancellation() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        var selected = fixture.ViewModel.SelectedProfile;
        fixture.Drafts.AllowLeave = false;
        await fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Transfers.InspectionCalls);
        Assert.Same(selected, fixture.ViewModel.SelectedProfile);

        fixture.Drafts.AllowLeave = true;
        fixture.Transfers.HasConflict = true;
        fixture.Dialogs.Result = ContentDialogResult.None;
        await fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Transfers.InspectionCalls);
        Assert.Equal(1, fixture.Dialogs.Calls);
        Assert.Equal(0, fixture.Transfers.ManifestImportCalls);
        Assert.Same(selected, fixture.ViewModel.SelectedProfile);
    });

    [Fact]
    public Task Catalog_profile_import_cancellation_during_inspection_keeps_the_previous_selection() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        var selected = fixture.ViewModel.SelectedProfile;
        fixture.Transfers.WaitDuringInspection = true;
        var import = fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);
        await fixture.Transfers.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.TransferViewModel.CancelCommand.Execute(null);
        await import.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(selected, fixture.ViewModel.SelectedProfile);
        Assert.Equal(0, fixture.Transfers.ManifestImportCalls);
        Assert.False(fixture.TransferViewModel.IsBusy);
        Assert.True(fixture.ViewModel.ImportProfileCommand.CanExecute(null));
    });

    [Fact]
    public Task Catalog_profile_import_installs_the_inspected_manifest_and_selects_the_actual_renamed_id() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        fixture.Picker.ProfilePath = "shared-profile.JSON";
        fixture.Transfers.HasConflict = true;
        fixture.Transfers.InstalledId = "sample-imported-2";
        fixture.Preferences.Values["sample-imported-2"] = "global-copy";

        await fixture.ViewModel.ImportProfileCommand.ExecuteAsync(null);

        Assert.Equal("shared-profile.JSON", fixture.Transfers.InspectedPath);
        Assert.Equal(1, fixture.Transfers.InspectionCalls);
        Assert.Equal(1, fixture.Transfers.ManifestImportCalls);
        Assert.Equal(0, fixture.Transfers.PathImportCalls);
        Assert.Same(fixture.Transfers.InspectedManifest, fixture.Transfers.InstalledManifest);
        Assert.Equal(ProfileImportConflictChoice.NewProfile, fixture.Transfers.ImportChoice);
        Assert.Equal("sample-imported-2", fixture.ViewModel.SelectedProfile?.Id);
        Assert.Equal("global-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        Assert.Contains(fixture.ViewModel.Profiles, profile => profile.Id == "sample");
        Assert.Contains(fixture.ViewModel.Profiles, profile => profile.Id == "sample-imported-2");
        Assert.Empty(fixture.Preferences.Saves); // Applying imported settings is not a new user choice.
    });

    [Fact]
    public Task Catalog_copy_format_restores_the_profile_preference_on_initial_load_and_profile_switch() => OnStaAsync(() =>
    {
        using var fixture = new ProfileWorkflowFixture();
        Assert.Equal("sample-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        fixture.SelectProfile("other");
        Assert.Equal("other-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        fixture.SelectProfile("sample");
        Assert.Equal("sample-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        Assert.Empty(fixture.Preferences.Saves);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Catalog_copy_format_user_selection_is_saved_only_for_the_current_profile() => OnStaAsync(() =>
    {
        using var fixture = new ProfileWorkflowFixture();
        fixture.ViewModel.SelectedOutputFormat = fixture.ViewModel.OutputFormats.Single(format => format.Id == "global-copy");

        Assert.Equal(("sample", "global-copy"), Assert.Single(fixture.Preferences.Saves));
        Assert.Equal("global-copy", fixture.Preferences.Get("sample"));
        Assert.Equal("other-copy", fixture.Preferences.Get("other"));
        fixture.SelectProfile("other");
        Assert.Equal("other-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        fixture.SelectProfile("sample");
        Assert.Equal("global-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        Assert.Single(fixture.Preferences.Saves);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Catalog_format_refresh_fallback_does_not_overwrite_the_saved_profile_preference() => OnStaAsync(() =>
    {
        using var fixture = new ProfileWorkflowFixture();
        fixture.Formats.RemoveAndNotify("sample-copy");
        Assert.True(fixture.ViewModel.SelectedOutputFormat?.IsBuiltIn);
        Assert.Equal("sample-copy", fixture.Preferences.Get("sample"));
        Assert.Empty(fixture.Preferences.Saves);

        fixture.Formats.AddAndNotify(new("sample-copy", "Restored sample format", "{FullPath}", "\n", "sample"));
        fixture.SelectProfile("other");
        fixture.SelectProfile("sample");
        Assert.Equal("sample-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        Assert.Empty(fixture.Preferences.Saves);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Catalog_pending_copy_format_choice_survives_refresh_and_profile_switch_before_disk_save() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        fixture.Preferences.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ViewModel.SelectedOutputFormat = fixture.ViewModel.OutputFormats.Single(format => format.Id == "global-copy");
        Assert.Equal("sample-copy", fixture.Preferences.Get("sample"));
        fixture.Formats.NotifyChanged();
        Assert.Equal("global-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        fixture.SelectProfile("other");
        fixture.SelectProfile("sample");
        Assert.Equal("global-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
        Assert.Single(fixture.Preferences.Saves);

        fixture.Preferences.SaveGate.TrySetResult();
        await WaitForAsync(() => fixture.Preferences.Get("sample") == "global-copy");
        Assert.Single(fixture.Preferences.Saves);
    });

    [Fact]
    public Task Catalog_repeated_copy_format_choice_keeps_the_latest_pending_request_when_an_older_identical_choice_finishes() => OnStaAsync(async () =>
    {
        using var fixture = new ProfileWorkflowFixture();
        var firstA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Preferences.SaveGates.Enqueue(firstA);
        fixture.Preferences.SaveGates.Enqueue(middleB);
        fixture.Preferences.SaveGates.Enqueue(latestA);
        var formatA = fixture.ViewModel.OutputFormats.Single(format => format.Id == "global-copy");
        var formatB = fixture.ViewModel.OutputFormats.Single(format => format.Id == "sample-copy");
        try
        {
            fixture.ViewModel.SelectedOutputFormat = formatA;
            fixture.ViewModel.SelectedOutputFormat = formatB;
            fixture.ViewModel.SelectedOutputFormat = formatA;
            Assert.Equal(3, fixture.Preferences.Saves.Count);

            firstA.TrySetResult();
            await WaitForAsync(() => fixture.Preferences.CompletedSaves == 1);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            middleB.TrySetResult();
            await WaitForAsync(() => fixture.Preferences.CompletedSaves == 2);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal("sample-copy", fixture.Preferences.Get("sample"));

            // Disk currently contains B. The still-pending final A is the user's
            // latest choice, even though the older A request has already finished.
            fixture.Formats.NotifyChanged();
            fixture.SelectProfile("other");
            fixture.SelectProfile("sample");
            Assert.Equal("global-copy", fixture.ViewModel.SelectedOutputFormat?.Id);
            Assert.Equal(3, fixture.Preferences.Saves.Count);
        }
        finally
        {
            firstA.TrySetResult();
            middleB.TrySetResult();
            latestA.TrySetResult();
            await WaitForAsync(() => fixture.Preferences.CompletedSaves == 3);
        }
        Assert.Equal("global-copy", fixture.Preferences.Get("sample"));
    });

    private sealed class ProfileWorkflowFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "catalog-profile-workflow-" + Guid.NewGuid().ToString("N"));
        public ProfileWorkflowFixture(IIndexedDatasetTransferService? datasets = null, bool attachCatalog = true)
        {
            var operation = new ApplicationOperationCoordinator(NullLogger<ApplicationOperationCoordinator>.Instance);
            var profiles = new WorkflowProfiles();
            Transfers = new WorkflowProfileTransfer(profiles);
            TransferViewModel = new WorkspaceTransferViewModel(Transfers, datasets!, new WorkflowRegistry(), null!, Picker,
                Dialogs, Notifications, operation, Drafts, null!, new TestAppLocalizer(), ImmediateUiScheduler.Instance,
                new AppPaths(_directory, Path.Combine(_directory, "settings.json")));
            Assert.True(TransferViewModel.IsInitialized);
            var databasePath = Path.Combine(_directory, "profile.db");
            ViewModel = new CatalogViewModel(new CatalogService(), profiles, new Workspace(databasePath),
                new DatabaseResolver(databasePath), Picker, null!, operation, new Notifier(), Notifications,
                Formats, null!, null!, null!, new TestAppLocalizer(), NullLogger<CatalogViewModel>.Instance,
                dispatcher: Dispatcher.CurrentDispatcher, outputPreferences: Preferences);
            if (attachCatalog) ViewModel.AttachWorkspaceTransfer(TransferViewModel, null!);
        }

        public CatalogViewModel ViewModel { get; }
        public WorkspaceTransferViewModel TransferViewModel { get; }
        public WorkflowProfileTransfer Transfers { get; }
        public WorkflowPicker Picker { get; } = new();
        public WorkflowDialogs Dialogs { get; } = new();
        public WorkflowDrafts Drafts { get; } = new();
        public WorkflowNotifications Notifications { get; } = new();
        public WorkflowFormats Formats { get; } = new();
        public WorkflowPreferences Preferences { get; } = new();
        public void SelectProfile(string id) => ViewModel.SelectedProfile = ViewModel.Profiles.Single(profile => profile.Id == id);
        public void Dispose()
        {
            Preferences.SaveGate?.TrySetResult();
            ViewModel.Dispose();
            TransferViewModel.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class WorkflowProfiles : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; private set; } = new([new WorkflowProfile("sample"), new WorkflowProfile("other")], [], DateTimeOffset.UtcNow);
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed;
        public void Add(string id)
        {
            var previous = Current;
            Current = new([.. Current.Profiles, new WorkflowProfile(id)], [], DateTimeOffset.UtcNow);
            Changed?.Invoke(this, new(previous, Current));
        }
    }

    private sealed class WorkflowProfile(string id) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(id, "1.0", id, ProfileCandidateKind.Directory, [], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
    }

    private sealed class WorkflowProfileTransfer(WorkflowProfiles profiles) : IProfileTransferService
    {
        public ProfileManifest InspectedManifest { get; } = new() { Id = "sample", DisplayName = "Imported profile" };
        public string InstalledId { get; set; } = "sample-imported";
        public bool HasConflict { get; set; }
        public bool WaitDuringInspection { get; set; }
        public Exception? InspectionError { get; set; }
        public Exception? ImportError { get; set; }
        public TaskCompletionSource InspectionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InspectionCalls { get; private set; }
        public int ManifestImportCalls { get; private set; }
        public int PathImportCalls { get; private set; }
        public string? InspectedPath { get; private set; }
        public ProfileManifest? InstalledManifest { get; private set; }
        public ProfileImportConflictChoice? ImportChoice { get; private set; }
        public async Task<ProfileTransferInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            InspectionCalls++;
            InspectedPath = path;
            InspectionStarted.TrySetResult();
            if (InspectionError is not null) throw InspectionError;
            if (WaitDuringInspection) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(InspectedManifest, HasConflict);
        }
        public Task<ProfileTransferResult?> ImportAsync(string path, ProfileImportConflictChoice choice = ProfileImportConflictChoice.NewProfile,
            CancellationToken cancellationToken = default)
        {
            PathImportCalls++;
            throw new InvalidOperationException("The inspected manifest must be installed without re-reading an untrusted file.");
        }
        public Task<ProfileTransferResult?> ImportManifestAsync(ProfileManifest manifest, ProfileImportConflictChoice choice,
            CancellationToken cancellationToken = default)
        {
            ManifestImportCalls++;
            if (ImportError is not null) throw ImportError;
            InstalledManifest = manifest;
            ImportChoice = choice;
            profiles.Add(InstalledId);
            return Task.FromResult<ProfileTransferResult?>(new(InstalledId, InstalledId + ".json"));
        }
        public Task ExportAsync(ProfileManifest manifest, string destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class WorkflowPicker : IDesktopPickerService
    {
        public string? ProfilePath { get; set; } = "profile.json";
        public int ProfilePickerCalls { get; private set; }
        public int WorkspacePickerCalls { get; private set; }
        public string? WorkspacePath { get; set; } = "profile.json";
        public string? PickProfileImportPath() { ProfilePickerCalls++; return ProfilePath; }
        public string? PickWorkspaceImportPath() { WorkspacePickerCalls++; return WorkspacePath; }
        public string? PickRootDirectory(string? initialDirectory) => null;
        public string? PickDatabasePath(string? currentPath) => null;
        public string? PickScanLogPath(string suggestedFileName) => null;
        public CatalogSelectionExportDestination? PickCatalogExportPath(string suggestedFileName) => null;
    }

    private sealed class WorkflowRegistry : IImportedWorkspaceRegistry
    {
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<ImportedWorkspaceSource>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImportedWorkspaceSource>>([]);
    }

    private sealed class WorkflowDrafts : IDraftProtectionService
    {
        public bool AllowLeave { get; set; } = true;
        public void Register(IDraftParticipant participant) { }
        public Task<bool> ConfirmAsync(IDraftParticipant participant) => Task.FromResult(AllowLeave);
        public Task<bool> ConfirmAllAsync() => Task.FromResult(AllowLeave);
    }

    private sealed class WorkflowDialogs : IContentDialogService
    {
        public ContentDialogResult Result { get; set; } = ContentDialogResult.Primary;
        public int Calls { get; private set; }
        public Queue<ContentDialogResult> Responses { get; } = new();
        public List<string> Contents { get; } = [];
        public void SetContentPresenter(ContentPresenter contentPresenter) { }
        public ContentPresenter? GetContentPresenter() => null;
        public void SetDialogHost(ContentPresenter dialogHost) { }
        public void SetDialogHost(ContentDialogHost dialogHost) { }
        public ContentPresenter? GetDialogHost() => null;
        public ContentDialogHost? GetDialogHostEx() => null;
        public Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken)
        { Calls++; Contents.Add(dialog.Content?.ToString() ?? ""); return Task.FromResult(Responses.TryDequeue(out var response) ? response : Result); }
    }

    private sealed class WorkflowNotifications : ISnackbarService
    {
        public List<string> Messages { get; } = [];
        public TimeSpan DefaultTimeOut { get; set; }
        public void SetSnackbarPresenter(SnackbarPresenter contentPresenter) { }
        public SnackbarPresenter? GetSnackbarPresenter() => null;
        public void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout) => Messages.Add(message);
    }

    private sealed class WorkflowFormats : ISelectionOutputFormatStore
    {
        private readonly List<SelectionOutputFormatDefinition> _formats = [SelectionOutputFormatDefaults.FullPathLines,
            new("global-copy", "Shared format", "{FullPath}", ",", null),
            new("sample-copy", "Sample format", "{FullPath}", "\n", "sample"),
            new("other-copy", "Other format", "{FullPath}", "\n", "other")];
        public event EventHandler? Changed;
        public IReadOnlyList<SelectionOutputFormatDefinition> GetAll() => _formats.ToArray();
        public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId) => _formats.Where(format =>
            format.ProfileId is null || string.Equals(profileId, format.ProfileId, StringComparison.OrdinalIgnoreCase)).ToArray();
        public void Save(SelectionOutputFormatDefinition definition) => throw new NotSupportedException();
        public bool Delete(string id) => throw new NotSupportedException();
        public void RemoveAndNotify(string id) { _formats.RemoveAll(format => format.Id == id); NotifyChanged(); }
        public void AddAndNotify(SelectionOutputFormatDefinition format) { _formats.Add(format); NotifyChanged(); }
        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class WorkflowPreferences : IProfileOutputPreferenceStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
            { ["sample"] = "sample-copy", ["other"] = "other-copy" };
        public List<(string ProfileId, string? FormatId)> Saves { get; } = [];
        public TaskCompletionSource? SaveGate { get; set; }
        public Queue<TaskCompletionSource> SaveGates { get; } = new();
        public int CompletedSaves { get; private set; }
        public string? Get(string profileId) => Values.GetValueOrDefault(profileId);
        public async Task SaveAsync(string profileId, string? formatId, CancellationToken cancellationToken = default)
        {
            Saves.Add((profileId, formatId));
            var gate = SaveGates.TryDequeue(out var nextGate) ? nextGate : SaveGate;
            if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
            if (formatId is null) Values.Remove(profileId); else Values[profileId] = formatId;
            CompletedSaves++;
        }
        public Task<bool> TryReplaceAsync(string profileId, string? expectedFormatId, string? replacementFormatId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
