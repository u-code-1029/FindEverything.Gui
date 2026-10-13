using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Views.Dialogs;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private bool _selectionImportOpen;
    private IReadOnlyList<ProfileSelectionMatchRuleManifest> SelectionRules =>
        WorkspaceTransfer?.CurrentSource is { } imported ? imported.FrozenProfile.SelectionMatchRules ?? []
        : SelectedProfile?.Profile.Descriptor.SelectionMatchRules ?? [];
    public bool CanOpenSelectionImport => !_disposed && !_selectionImportOpen && !IsBusy
        && _projectionQuery is ICatalogSelectionMatchQuery && _displayedProjectionSnapshot is { IsBuilding: false }
        && SelectionRules.Count > 0;

    [RelayCommand(CanExecute = nameof(CanOpenSelectionImport))]
    private Task OpenSelectionImportAsync() => OpenSelectionImportCoreAsync(false);
    public Task OpenSelectionImportFromClipboardAsync() => OpenSelectionImportCoreAsync(true);

    private async Task OpenSelectionImportCoreAsync(bool fromClipboard)
    {
        if (!CanOpenSelectionImport || _projectionQuery is not { } query || query is not ICatalogSelectionMatchQuery matcher) return;
        var snapshot = _displayedProjectionSnapshot!;
        var version = Volatile.Read(ref _queryVersion);
        var definition = _displayedQueryDefinition;
        var source = WorkspaceTransfer?.CurrentSource;
        var selectedProfile = SelectedProfile;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_outputLifetime.Token);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (!Current()) lifetime.Cancel();
        }
        bool Current() => !_disposed && ReferenceEquals(query, _projectionQuery)
            && version == Volatile.Read(ref _queryVersion) && !IsBusy
            && ReferenceEquals(snapshot, _displayedProjectionSnapshot)
            && Equals(definition, _displayedQueryDefinition) && ReferenceEquals(source, WorkspaceTransfer?.CurrentSource)
            && ReferenceEquals(selectedProfile, SelectedProfile);
        PropertyChanged += Changed;
        _selectionImportOpen = true; OpenSelectionImportCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanOpenSelectionImport));
        try
        {
            await CommitTrackedNotesAsync();
            if (!Current()) throw new OperationCanceledException();
            using var viewModel = new SelectionImportViewModel(SelectionRules, _localizer, async (rule, document, token) =>
            {
                if (!Current()) throw new OperationCanceledException(token);
                var input = new CatalogSelectionMatchInput(rule.Columns.Select(column => new CatalogSelectionMatchColumn(
                    column.FieldId, column.ValueSource, column.DateComparison, column.DateFormat)).ToArray(),
                    document.ReadRowsAsync(token), _localizer.Culture.Name);
                var result = await matcher.CreateSelectionMatchAsync(input, cancellationToken: token);
                if (!Current()) { result.Dispose(); throw new OperationCanceledException(token); }
                return result;
            }, lifetime.Token);
            if (fromClipboard)
            {
                if (Clipboard.ContainsData(DataFormats.Html) && Clipboard.GetData(DataFormats.Html) is string { Length: > 0 } html) viewModel.SetClipboardInput(html, true);
                else if (Clipboard.ContainsText()) viewModel.SetClipboardInput(Clipboard.GetText(), false);
            }
            var dialog = new SelectionImportDialog(viewModel) { Owner = System.Windows.Application.Current?.MainWindow };
            if (dialog.ShowDialog() != true) return;
            using var result = viewModel.TakeResult();
            if (result is null || !Current() || lifetime.IsCancellationRequested)
                throw new OperationCanceledException();
            // The preview can outlive an external publisher or note/file edit.
            // Count opens a fresh transaction and validates generation, cell and
            // note revisions before the existing check state is replaced.
            await query.CountAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException();
            CancelSelectionRequests();
            _querySelection.ReplaceBaseline(result.Keys); _selectionAnchor = -1;
            UpdateProjectionSelection();
            SetStatus(L("Loc.SelectionImport.Completed", "데이터로 선택 완료"),
                F("Loc.SelectionImport.Completed.Message", "현재 검색 결과에서 {0:N0}개 항목을 체크했습니다.", result.Summary.MatchingItems), InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException)
        { if (!_disposed) SetStatus(L("Loc.Common.Cancelled", "취소됨"), L("Loc.SelectionImport.QueryChanged", "검색 결과가 변경되어 적용하지 않았습니다. 기존 체크는 유지됩니다."), InfoBarSeverity.Informational); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not apply data-based catalog selection.");
            if (!_disposed) SetStatus(L("Loc.SelectionImport.Failed", "데이터로 선택 실패"), exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            PropertyChanged -= Changed;
            _selectionImportOpen = false;
            OpenSelectionImportCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanOpenSelectionImport));
        }
    }

    private void NotifySelectionImportAvailability()
    { OpenSelectionImportCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanOpenSelectionImport)); }
}
