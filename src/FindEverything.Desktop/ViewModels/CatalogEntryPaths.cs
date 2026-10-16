using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private bool _updatingEntryPaths;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPathsSummary))]
    [NotifyPropertyChangedFor(nameof(EntryPathsDescription))]
    private IReadOnlyList<string> _entryPaths = [];

    [ObservableProperty]
    private string _newEntryPath = string.Empty;

    public string EntryPathsSummary => EntryPaths.Count switch
    {
        0 => L("Loc.Catalog.Root.None", "선택한 경로 없음"),
        1 => EntryPaths[0],
        _ => F("Loc.Catalog.Entries.Count", "시작 경로 {0}개", EntryPaths.Count),
    };
    public string EntryPathsDescription => string.Join(Environment.NewLine, EntryPaths);

    private void ReplaceEntryPaths(IEnumerable<string> paths, bool persist)
    {
        _updatingEntryPaths = true;
        try
        {
            EntryPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            RootPath = EntryPaths.FirstOrDefault();
        }
        finally { _updatingEntryPaths = false; }
        if (!persist) return;
        _automaticLoadSuppressed = false;
        _lastAutomaticLoadKey = null;
        ScheduleWorkspacePersistence(WorkspaceDraftFields.RootPath);
    }

    private void RestoreEntryPaths(WorkspaceSnapshot snapshot)
    {
        var saved = SelectedProfile is null ? null
            : ProfileEntryPathSnapshots.Get(snapshot.ProfileEntryPaths, SelectedProfile.Id);
        // Migrate the old global path only until profile-specific locations exist.
        ReplaceEntryPaths(saved ?? ((snapshot.ProfileEntryPaths?.Count ?? 0) == 0
            && !string.IsNullOrWhiteSpace(snapshot.RootPath) ? [snapshot.RootPath] : []), false);
    }

    [RelayCommand]
    private void OpenEntryPaths()
    {
        if (CanEditWorkspace) IsDetailsOpen = true;
    }

    [RelayCommand]
    private async Task BrowseEntryPathAsync()
    {
        if (!CanEditWorkspace || SelectedProfile is null) return;
        var profileId = SelectedProfile.Id;
        var path = await _pickerService.PickRootDirectoryAsync(RootPath);
        if (path is not null && CanEditWorkspace && SelectedProfile?.Id == profileId)
            await AddEntryPathCoreAsync(path);
    }

    [RelayCommand]
    private Task AddEntryPathAsync() => AddEntryPathCoreAsync(NewEntryPath);

    private async Task AddEntryPathCoreAsync(string path)
    {
        if (!CanEditWorkspace || SelectedProfile is null) return;
        try
        {
            path = path.Trim();
            if (!Path.IsPathFullyQualified(path) || !ProfileWorkspaceManifestValidator.IsAbsoluteEntryPath(path))
                throw new ArgumentException(L("Loc.Catalog.Entries.Absolute", "드라이브 문자 또는 UNC 주소로 시작하는 절대경로를 입력하세요."));
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (EntryPaths.Any(existing => SameWorkspacePath(existing, path)))
                throw new ArgumentException(L("Loc.Catalog.Entries.Duplicate", "이미 등록된 시작 경로입니다."));
            if (EntryPaths.Count >= ProfileWorkspaceManifestValidator.MaximumEntryPaths)
                throw new ArgumentException(F("Loc.Catalog.Entries.Limit", "시작 경로는 최대 {0}개까지 등록할 수 있습니다.", ProfileWorkspaceManifestValidator.MaximumEntryPaths));
            ReplaceEntryPaths(EntryPaths.Append(path), true);
            NewEntryPath = string.Empty;
            await CommitWorkspaceAsync();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetStatus(L("Loc.Catalog.Entries.Title", "시작 경로"), exception.Message, InfoBarSeverity.Warning);
        }
    }

    [RelayCommand]
    private async Task RemoveEntryPathAsync(string? path)
    {
        if (!CanEditWorkspace || SelectedProfile is null || path is null) return;
        ReplaceEntryPaths(EntryPaths.Where(existing => !SameWorkspacePath(existing, path)), true);
        await CommitWorkspaceAsync();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? MergeProfileEntryPaths(
        WorkspaceSnapshot current, WorkspaceSnapshot requested) =>
        string.IsNullOrWhiteSpace(requested.SelectedProfileId) ? current.ProfileEntryPaths
            : ProfileEntryPathSnapshots.Replace(current.ProfileEntryPaths, requested.SelectedProfileId,
                ProfileEntryPathSnapshots.Get(requested.ProfileEntryPaths, requested.SelectedProfileId));
}
