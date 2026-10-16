using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfileEntryPathDraftViewModel : ObservableObject
{
    [ObservableProperty] private string _path = string.Empty;
}

public partial class ProfilesViewModel
{
    private readonly IWorkspaceContext? _entryPathWorkspace;
    private readonly IDesktopPickerService? _entryPathPicker;
    private bool _entryPathsConfigured;
    private bool _entryPathsDirty;
    private bool _entryPathsNeedInstall;
    private CancellationTokenSource? _entryPathBrowseCancellation;
    public ObservableCollection<ProfileEntryPathDraftViewModel> DraftEntryPaths { get; } = [];
    public event Action<ProfileEntryPathDraftViewModel>? EntryPathEditorRequested;

    public string EntryPathsSummary => F("Loc.Profiles.EntryPaths.Summary", "시작 경로 {0} / {1}개",
        DraftEntryPaths.Count, ProfileWorkspaceManifestValidator.MaximumEntryPaths);

    private void ResetDraftEntryPaths(IReadOnlyList<string>? values)
    {
        foreach (var row in DraftEntryPaths) row.PropertyChanged -= OnEntryPathDraftChanged;
        DraftEntryPaths.Clear();
        _entryPathsConfigured = values is not null;
        _entryPathsDirty = false;
        _entryPathsNeedInstall = false;
        foreach (var value in values ?? [])
        {
            var row = new ProfileEntryPathDraftViewModel { Path = value };
            row.PropertyChanged += OnEntryPathDraftChanged;
            DraftEntryPaths.Add(row);
        }
        NotifyEntryPathAvailability();
    }

    private void LoadDraftEntryPaths(ProfileManifest manifest)
    {
        var local = _entryPathWorkspace is not null && !string.IsNullOrWhiteSpace(manifest.Id)
            ? ProfileEntryPathSnapshots.Get(_entryPathWorkspace.Current.ProfileEntryPaths, manifest.Id) : null;
        ResetDraftEntryPaths(local ?? manifest.Workspace?.EntryPaths);
        _entryPathsNeedInstall = local is null && manifest.Workspace?.EntryPaths is not null;
    }

    private void OnEntryPathDraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProfileEntryPathDraftViewModel.Path)) MarkEntryPathsChanged();
    }

    private void MarkEntryPathsChanged()
    {
        if (_isPopulatingDraft) return;
        _entryPathsConfigured = true;
        _entryPathsDirty = true;
        MarkDraftChanged(refreshStructure: false);
        NotifyEntryPathAvailability();
    }

    private void NotifyEntryPathAvailability()
    {
        OnPropertyChanged(nameof(EntryPathsSummary));
        AddEntryPathCommand.NotifyCanExecuteChanged();
        BrowseEntryPathCommand.NotifyCanExecuteChanged();
        RemoveEntryPathCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddEntryPath() => !_disposed && !IsEditorBusy
        && DraftEntryPaths.Count < ProfileWorkspaceManifestValidator.MaximumEntryPaths;
    private bool CanBrowseEntryPath() => !_disposed && !IsEditorBusy && _entryPathPicker is not null;
    private bool CanRemoveEntryPath() => !_disposed && !IsEditorBusy;

    [RelayCommand(CanExecute = nameof(CanAddEntryPath))]
    private void AddEntryPath()
    {
        if (!CanAddEntryPath()) return;
        var row = new ProfileEntryPathDraftViewModel();
        row.PropertyChanged += OnEntryPathDraftChanged;
        DraftEntryPaths.Add(row);
        MarkEntryPathsChanged();
        EntryPathEditorRequested?.Invoke(row);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveEntryPath))]
    private void RemoveEntryPath(ProfileEntryPathDraftViewModel? row)
    {
        if (!CanRemoveEntryPath() || row is null || !DraftEntryPaths.Remove(row)) return;
        row.PropertyChanged -= OnEntryPathDraftChanged;
        MarkEntryPathsChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBrowseEntryPath))]
    private async Task BrowseEntryPathAsync(ProfileEntryPathDraftViewModel? row)
    {
        if (!CanBrowseEntryPath() || row is null && !CanAddEntryPath()) return;
        using var cancellation = new CancellationTokenSource();
        _entryPathBrowseCancellation = cancellation;
        var revision = _editorSession.Revision;
        try
        {
            var selected = await _entryPathPicker!.PickRootDirectoryAsync(row?.Path,
                cancellation.Token).ConfigureAwait(true);
            if (_disposed || cancellation.IsCancellationRequested || revision != _editorSession.Revision
                || IsEditorBusy || string.IsNullOrWhiteSpace(selected)) return;
            if (row is not null)
            {
                if (DraftEntryPaths.Contains(row)) row.Path = selected;
            }
            else if (CanAddEntryPath())
            {
                var added = new ProfileEntryPathDraftViewModel { Path = selected };
                added.PropertyChanged += OnEntryPathDraftChanged;
                DraftEntryPaths.Add(added);
                MarkEntryPathsChanged();
                EntryPathEditorRequested?.Invoke(added);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && revision == _editorSession.Revision)
                SetEditorStatus(exception.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally { if (ReferenceEquals(_entryPathBrowseCancellation, cancellation)) _entryPathBrowseCancellation = null; }
    }

    private async Task SaveEntryPathsAsync(string profileId)
    {
        if (_entryPathWorkspace is null || !_entryPathsConfigured || !(_entryPathsDirty || _entryPathsNeedInstall)) return;
        await _entryPathWorkspace.SetProfileEntryPathsAsync(profileId,
            DraftEntryPaths.Select(row => row.Path.Trim()).ToArray()).ConfigureAwait(true);
        _entryPathsDirty = false;
        _entryPathsNeedInstall = false;
    }

    private void OnEntryPathsWorkspaceChanged(object? sender, WorkspaceChangedEventArgs args)
    {
        var profileId = _originalProfileId;
        if (_disposed || profileId is null || _entryPathsDirty) return;
        var previous = ProfileEntryPathSnapshots.Get(args.Previous.ProfileEntryPaths, profileId);
        var current = ProfileEntryPathSnapshots.Get(args.Current.ProfileEntryPaths, profileId);
        if (ProfileEntryPathSnapshots.Equal(previous, current)) return;
        _uiScheduler.Post(() =>
        {
            if (!_disposed && !_entryPathsDirty && profileId == _originalProfileId)
                ResetDraftEntryPaths(current);
        });
    }

    private void DetachEntryPathSubscriptions()
    {
        _entryPathBrowseCancellation?.Cancel();
        if (_entryPathWorkspace is not null) _entryPathWorkspace.Changed -= OnEntryPathsWorkspaceChanged;
        foreach (var row in DraftEntryPaths) row.PropertyChanged -= OnEntryPathDraftChanged;
    }
}
