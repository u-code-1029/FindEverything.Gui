using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using FindEverything.Application.Catalog;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Services;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel : IWorkspaceExportSource
{
    public WorkspaceTransferViewModel? WorkspaceTransfer { get; private set; }
    private IIndexedDatasetTransferService? _datasetTransfer;
    private ICatalogProjectionStore? _importedProjectionStore;
    private CancellationTokenSource? _sharedLoadCancellation;
    private bool _applyingSharedSource;
    public bool IsReadOnlyDataSource => WorkspaceTransfer?.IsImported == true;
    private ICatalogProjectionStore? ActiveProjectionStore => _importedProjectionStore ?? _projectionStore;

    public void AttachWorkspaceTransfer(WorkspaceTransferViewModel transfer, IIndexedDatasetTransferService datasets)
    {
        WorkspaceTransfer = transfer; _datasetTransfer = datasets;
        transfer.SourceChanged += OnSharedSourceChanged;
        transfer.PropertyChanged += OnTransferStateChanged;
        OnPropertyChanged(nameof(WorkspaceTransfer));
        if (transfer.IsImported) _ = ApplySharedSourceAsync();
    }

    private void OnSharedSourceChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess()) _ = ApplySharedSourceAsync();
        else _dispatcher.BeginInvoke(new Action(() => _ = ApplySharedSourceAsync()));
    }
    private void OnTransferStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(WorkspaceTransferViewModel.IsBusy) and not nameof(WorkspaceTransferViewModel.IsInitialized)) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            ScanCommand.NotifyCanExecuteChanged(); LoadCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanEditWorkspace));
            if (WorkspaceTransfer?.IsBusy == false && !IsReadOnlyDataSource) ScheduleAutomaticLoad();
        }));
    }

    private async Task ApplySharedSourceAsync()
    {
        if (_disposed) return;
        _sharedLoadCancellation?.Cancel();
        using var source = new CancellationTokenSource(); _sharedLoadCancellation = source;
        var selected = WorkspaceTransfer?.CurrentSource;
        try
        {
            _pendingScanCancellation?.Cancel(); _projectionCancellation?.Cancel(); _queryCancellation?.Cancel();
            CancelWorkspacePersistenceDebounce(); _lastAutomaticLoadKey = null;
            _automaticLoadSuppressed = true;
            if (_operationCoordinator.IsRunning && WorkspaceTransfer?.IsBusy != true)
                await _operationCoordinator.CancelAndWaitAsync(TimeSpan.FromSeconds(30), source.Token);
            source.Token.ThrowIfCancellationRequested();
            await CommitTrackedNotesAsync();
            source.Token.ThrowIfCancellationRequested();
            if (selected?.CatalogSnapshot is { } saved && !string.Equals(saved.Scope.CultureName, _localizer.Culture.Name, StringComparison.OrdinalIgnoreCase))
            {
                IsBusy = true; IsLoadingExistingIndex = true;
                ProgressMessage = L("Loc.Transfer.Culture.Preparing", "현재 언어에 맞게 공유 데이터의 검색 정보를 준비하는 중…");
                selected = await WorkspaceTransfer!.EnsureSourceCultureAsync(selected, source.Token);
                source.Token.ThrowIfCancellationRequested();
            }
            ReleaseProjectionQuery();
            ResetLiveFilters();
            if (_importedProjectionStore is IDisposable oldStore) oldStore.Dispose();
            _importedProjectionStore = null;
            _projectionSnapshot = null; _displayedProjectionSnapshot = null;
            FilterText = string.Empty;
            _queryDefinition = new() { TextPreviewCharacters = 2048 };
            RebuildActiveColumnFilters();
            _applyingSharedSource = true; _isApplyingWorkspaceSnapshot = true;
            try
            {
                if (selected is not null)
                {
                    SelectedProfile = Profiles.FirstOrDefault(x => x.Id.Equals(selected.ProfileId, StringComparison.OrdinalIgnoreCase));
                    RootPath = selected.EffectiveRoot; DatabasePath = selected.MetadataDatabasePath;
                    // Even if this profile has subsequently been edited, a shared snapshot retains its frozen columns.
                    if (selected.CatalogSnapshot is { } snapshot)
                    {
                        _importedProjectionStore = _datasetTransfer!.OpenCatalogStore(selected);
                        _projectionSnapshot = snapshot; Fields = snapshot.Fields;
                    }
                    HasValueMappings = Fields.Any(field => field.HasValueMappings);
                }
                else
                {
                    ApplyProfileSnapshot(_profileCatalog.Current, _workspaceContext.Current.SelectedProfileId);
                    Fields = SelectedProfile?.Profile.Descriptor.Fields.OrderBy(field => field.Order).ToArray() ?? [];
                    ApplyWorkspaceSnapshot(_workspaceContext.Current);
                }
            }
            finally { _isApplyingWorkspaceSnapshot = false; _applyingSharedSource = false; }
            OnPropertyChanged(nameof(IsReadOnlyDataSource));
            OnPropertyChanged(nameof(CanEditWorkspace));
            ScanCommand.NotifyCanExecuteChanged(); LoadCommand.NotifyCanExecuteChanged();
            RefreshTextColumnCommand.NotifyCanExecuteChanged();
            if (selected is not null)
            {
                _loadedItems.Clear(); Items = CreateItemsView(_loadedItems); HasLoadedItems = false; SetSelection([]);
                if (selected.HasCatalog)
                {
                    IsBusy = true; IsLoadingExistingIndex = true;
                    await RefreshProjectionQueryAsync(force: true, cancellationToken: source.Token);
                    SetStatus(L("Loc.Transfer.Source.ReadOnly", "공유 데이터 · 읽기 전용"),
                        selected.HasPendingScopes ? L("Loc.Transfer.Source.Partial", "일부 범위만 포함된 공유 데이터입니다.") : selected.EffectiveRoot,
                        InfoBarSeverity.Informational);
                }
                else SetStatus(L("Loc.Transfer.Catalog.Missing", "구조화 결과 미포함"),
                    L("Loc.Transfer.Catalog.Missing.Help", "파일 찾기에서 메타데이터를 조회할 수 있습니다. 구조화 결과를 만들려면 로컬 결과로 전환한 뒤 직접 스캔하세요."), InfoBarSeverity.Informational);
            }
            else { _automaticLoadSuppressed = false; ScheduleAutomaticLoad(); }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) { SetStatus(L("Loc.Catalog.Status.Error", "오류"), exception.Message, InfoBarSeverity.Error); }
        finally
        {
            if (ReferenceEquals(_sharedLoadCancellation, source))
            {
                _sharedLoadCancellation = null;
                IsLoadingExistingIndex = false; IsBusy = false;
                ApplyPendingProfileSnapshot();
            }
        }
    }

    public WorkspaceExportContext? CaptureWorkspaceExport() => IsBusy || IsReadOnlyDataSource || _projectionSnapshot is not { } snapshot
        ? null : new(snapshot.Scope.RootPath, snapshot.Scope.DatabasePath, snapshot.Scope.ProfileId,
            (_projectionStore as FindEverything.Infrastructure.Persistence.SqliteCatalogProjectionStore)?.DatabasePath, snapshot);

    private void DetachWorkspaceTransfer()
    {
        _sharedLoadCancellation?.Cancel();
        if (WorkspaceTransfer is not null)
        {
            WorkspaceTransfer.SourceChanged -= OnSharedSourceChanged;
            WorkspaceTransfer.PropertyChanged -= OnTransferStateChanged;
        }
        if (_importedProjectionStore is IDisposable store) store.Dispose();
    }
}
