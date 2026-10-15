using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Sharing;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private ProfileTransferResult? _profileToActivateAfterSourceChange;
    public bool CanImportProfile => !_disposed && CanEditWorkspace
        && WorkspaceTransfer is { IsInitialized: true, IsBusy: false };

    [RelayCommand(CanExecute = nameof(CanImportProfile))]
    private async Task ImportProfileAsync()
    {
        if (!CanImportProfile) return;
        try
        {
            var path = _pickerService.PickProfileImportPath();
            if (path is null || !CanImportProfile) return;
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L("Loc.Ux.Catalog.ImportProfile.JsonOnly", "프로필 JSON 파일을 선택하세요."));
            await WorkspaceTransfer!.OpenPathAsync(path);
        }
        catch (Exception exception)
        {
            SetStatus(L("Loc.Ux.Catalog.ImportProfile", "프로필 JSON 불러오기"), exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OnProfileImported(object? sender, ProfileTransferResult imported)
    {
        void Activate()
        {
            if (_disposed) return;
            WorkspaceTransfer?.TakePendingProfileActivation();
            if (IsReadOnlyDataSource)
            {
                _profileToActivateAfterSourceChange = imported;
                // Source switching finishes its own asynchronous cleanup before activation.
                WorkspaceTransfer!.SelectedSource = WorkspaceTransfer.Sources[0];
                return;
            }
            ApplyProfileSnapshot(_profileCatalog.Current, imported.ProfileId);
            SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(
                profile.Id, imported.ProfileId, StringComparison.OrdinalIgnoreCase));
            RefreshOutputFormats(preferSaved: true);
        }
        if (_dispatcher.CheckAccess()) Activate();
        else _dispatcher.BeginInvoke(new Action(Activate));
    }
}
