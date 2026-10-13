namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel
{
    public WorkspaceTransferViewModel? WorkspaceTransfer { get; private set; }
    public object? ExportProfileCandidate => SelectedEditableProfile?.Id ?? SelectedProfile?.Id;
    partial void OnSelectedEditableProfileChanged(Services.EditableProfileSummary? value) => OnPropertyChanged(nameof(ExportProfileCandidate));
    public void AttachWorkspaceTransfer(WorkspaceTransferViewModel transfer)
    {
        WorkspaceTransfer = transfer;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedProfile)) OnPropertyChanged(nameof(ExportProfileCandidate)); };
        OnPropertyChanged(nameof(WorkspaceTransfer));
    }
}
