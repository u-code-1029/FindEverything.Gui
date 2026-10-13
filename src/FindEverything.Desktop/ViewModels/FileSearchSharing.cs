using FindEverything.Application.Sharing;

namespace FindEverything.Desktop.ViewModels;

public partial class FileSearchViewModel : IWorkspaceExportSource
{
    public WorkspaceTransferViewModel? WorkspaceTransfer { get; private set; }
    public bool IsReadOnlyDataSource => WorkspaceTransfer?.IsImported == true;
    public void AttachWorkspaceTransfer(WorkspaceTransferViewModel transfer)
    {
        WorkspaceTransfer = transfer;
        transfer.SourceChanged += OnSharedSourceChanged;
        transfer.PropertyChanged += OnTransferStateChanged;
        OnPropertyChanged(nameof(WorkspaceTransfer));
        if (transfer.IsImported) ApplySharedSource();
    }
    private void OnSharedSourceChanged(object? sender, EventArgs e) => Dispatch(ApplySharedSource);
    private void OnTransferStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceTransferViewModel.IsBusy) or nameof(WorkspaceTransferViewModel.IsInitialized)) Dispatch(() =>
        {
            RefreshIndexCommand.NotifyCanExecuteChanged(); BrowseRootCommand.NotifyCanExecuteChanged(); BrowseDatabaseCommand.NotifyCanExecuteChanged();
        });
    }
    private void ApplySharedSource()
    {
        if (_disposed) return;
        _scanCancellation?.Cancel(); InvalidateScope(); ClearSearchResults();
        if (WorkspaceTransfer?.CurrentSource is { } source)
        {
            RootPath = source.EffectiveRoot;
            DatabasePath = source.MetadataDatabasePath;
            IsUsingDefaultDatabasePath = false;
        }
        else
        {
            var workspace = _workspaceContext.Current;
            RootPath = workspace.RootPath;
            DatabasePath = _databasePathResolver.ResolveFileSearchDatabasePath(workspace);
            IsUsingDefaultDatabasePath = string.IsNullOrWhiteSpace(workspace.DatabasePath);
        }
        OnPropertyChanged(nameof(IsReadOnlyDataSource));
        RefreshIndexCommand.NotifyCanExecuteChanged(); BrowseRefreshScopeCommand.NotifyCanExecuteChanged();
        RefreshScopeCommand.NotifyCanExecuteChanged(); RefreshItemScopeCommand.NotifyCanExecuteChanged();
        BrowseRootCommand.NotifyCanExecuteChanged(); BrowseDatabaseCommand.NotifyCanExecuteChanged();
        UseDefaultDatabasePathCommand.NotifyCanExecuteChanged();
        _ = InitializeAsync();
    }
    public WorkspaceExportContext? CaptureWorkspaceExport() => IsBusy || IsReadOnlyDataSource || !HasIndex
        || string.IsNullOrWhiteSpace(RootPath) ? null : new(RootPath, DatabasePath);
    private void DetachWorkspaceTransfer()
    {
        if (WorkspaceTransfer is null) return;
        WorkspaceTransfer.SourceChanged -= OnSharedSourceChanged;
        WorkspaceTransfer.PropertyChanged -= OnTransferStateChanged;
    }
}
