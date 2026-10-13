using FindEverything.Application.Catalog;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    public async Task<bool> SaveTextFileEditAsync(CatalogTextFileEditContext context,
        Func<CancellationToken, Task<TextFileWriteResult>> write, CancellationToken cancellationToken = default)
    {
        ValidateTextFileEditContext(context);
        var snapshot = _projectionSnapshot ?? throw new InvalidOperationException("No local result is loaded.");
        var store = _projectionStore ?? throw new InvalidOperationException("No local result store is available.");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _outputLifetime.Token);
        _projectionCancellation = source;
        CatalogProjectionSnapshot? updated = null;
        IsBusy = true;
        try
        {
            await CommitTrackedNotesAsync();
            await _operationCoordinator.RunAsync(ApplicationOperationKind.IndexWrite, async token =>
            {
                if (IsReadOnlyDataSource || !ReferenceEquals(SelectedProfile?.Profile, context.Profile)
                    || _projectionSnapshot?.Generation != context.Generation || _projectionSnapshot.Revision != context.Revision)
                    throw new InvalidOperationException("The file edit context changed before saving.");
                var saved = await write(token).ConfigureAwait(false);
                try
                {
                    // The actual file has committed. Finish this single-cell cache
                    // transaction even if the user closes the editor immediately.
                    updated = await store.UpdateTextCellAsync(snapshot, context.Row.FullPath,
                        context.Field.FieldId, saved.Content, saved.Resource, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                { _logger.LogWarning(exception, "The file was saved, but its structured cache could not be updated."); }
            }, source.Token);
            if (updated is not null && !_disposed && !IsReadOnlyDataSource
                && ReferenceEquals(SelectedProfile?.Profile, context.Profile)
                && _projectionSnapshot?.Generation == context.Generation)
            {
                _projectionSnapshot = updated;
                try
                {
                    var matches = await store.FindMatchingPathsAsync(updated, _displayedQueryDefinition,
                        [context.Row.FullPath], CancellationToken.None);
                    if (matches is not null && !matches.Contains(context.Row.FullPath))
                        _querySelection.RemoveUnavailableKey(context.Row.SelectionKey);
                    await RefreshProjectionQueryAsync(preserveSelection: true, force: true, allowRevisionChange: true);
                }
                catch (Exception exception) { _logger.LogWarning(exception, "Could not display the saved file value."); return false; }
                OnPropertyChanged(nameof(ResultSourceDescription));
            }
            return updated is not null;
        }
        finally
        {
            if (ReferenceEquals(_projectionCancellation, source)) _projectionCancellation = null;
            IsBusy = false;
        }
    }

    public async Task CommitTextFileEditAsync(CatalogItemViewModel row, string fieldId,
        string newContent, ProfileFieldResourceInfo resource, CancellationToken cancellationToken = default)
    {
        if (IsReadOnlyDataSource || !CanEditWorkspace || _projectionSnapshot is not { IsBuilding: false } snapshot
            || _projectionStore is null || _displayedProjectionSnapshot?.Generation != snapshot.Generation
            || _displayedProjectionSnapshot.Revision != snapshot.Revision)
            throw new InvalidOperationException("The local result changed while saving the file.");
        var store = _projectionStore;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _outputLifetime.Token);
        _projectionCancellation = source;
        IsBusy = true;
        try
        {
            await CommitTrackedNotesAsync();
            CatalogProjectionSnapshot? updated = null;
            await _operationCoordinator.RunAsync(ApplicationOperationKind.IndexWrite, async token =>
            {
                updated = await store.UpdateTextCellAsync(snapshot, row.FullPath, fieldId, newContent, resource, token).ConfigureAwait(false);
            }, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (_projectionSnapshot?.Generation != snapshot.Generation || IsReadOnlyDataSource)
                throw new InvalidOperationException("The data source changed while updating the file cache.");
            _projectionSnapshot = updated!;
            await RefreshProjectionQueryAsync(force: true);
            OnPropertyChanged(nameof(ResultSourceDescription));
        }
        finally
        {
            if (ReferenceEquals(_projectionCancellation, source)) _projectionCancellation = null;
            IsBusy = false;
        }
    }
}
