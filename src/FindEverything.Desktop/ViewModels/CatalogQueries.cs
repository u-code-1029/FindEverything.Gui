using System.Globalization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Results;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private readonly ICatalogProjectionStore? _projectionStore;
    private readonly IIndexSessionFactory? _indexSessions;
    private CatalogProjectionSnapshot? _projectionSnapshot;
    private CatalogProjectionSnapshot? _displayedProjectionSnapshot;
    private IResultQuerySession<CatalogProjectionRow>? _projectionQuery;
    private VirtualResultCollection<CatalogItemViewModel>? _virtualItems;
    private readonly QuerySelection _querySelection = new();
    private ResultSelectionController? _selectionController;
    private ResultSelectionController SelectionController => _selectionController ??= new(_querySelection);
    private long _selectionRevision;
    private CatalogProjectionQuery _queryDefinition = new() { TextPreviewCharacters = 2048 };
    private CatalogProjectionQuery _displayedQueryDefinition = new();
    private CancellationTokenSource? _projectionCancellation;
    private CancellationTokenSource? _queryCancellation;
    private readonly CancellationTokenSource _outputLifetime = new();
    private int _selectionAnchor = -1;
    private long _matchingCount;
    private bool _hasExactResultCount = true;
    private long _queryVersion;
    private bool _isRecoveringProjection;
    private int _activeNoteEdits;
    private bool _deferredQueryRefresh;
    private bool _deferredPreserveSelection;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyCanExecuteChangedFor(nameof(InvertSelectionCommand))]
    private bool _isResultCountPending;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _appliedHighlightQuery = string.Empty;

    public void BeginUserInputEdit() => _activeNoteEdits++;
    public void EndUserInputEdit()
    {
        _activeNoteEdits = Math.Max(0, _activeNoteEdits - 1);
        if (_activeNoteEdits == 0 && _liveRefreshQueued) ScheduleLiveFilterRefresh();
        if (_activeNoteEdits != 0 || !_deferredQueryRefresh) return;
        var preserve = _deferredPreserveSelection;
        _deferredQueryRefresh = false;
        _ = RefreshProjectionQueryAsync(preserve);
    }

    public bool UsesQuerySelection => _projectionQuery is not null;
    public string ResultSourceDescription => _displayedProjectionSnapshot is { } snapshot
        ? $"{snapshot.Scope.RootPath} · {snapshot.Scope.DatabasePath} · {snapshot.Summary.UpdatedUtc.ToLocalTime():g}"
        : string.Empty;

    private CatalogProjectionScope CreateProjectionScope(WorkspaceSelection workspace, Guid? scanId, ILoadedProfile profile)
    {
        var fingerprint = CatalogDataFingerprint.Create(profile.Descriptor, profile.CacheIdentity);
        var identity = string.Join("|", workspace.ProfileId, workspace.RootPath.ToUpperInvariant(),
            workspace.DatabasePath.ToUpperInvariant(), fingerprint, _localizer.Culture.Name);
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            workspace.ProfileId, workspace.RootPath, workspace.DatabasePath, fingerprint,
            _localizer.Culture.Name, scanId);
    }

    private async Task RunProjectionOperationAsync(string name, CatalogOperationMode mode, bool automatic,
        CancellationToken cancellationToken = default, bool allowBuildFromIndex = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsBusy) return;
        if (_operationCoordinator.IsRunning &&
            !(mode == CatalogOperationMode.Scan && _operationCoordinator.CurrentKind == ApplicationOperationKind.IndexLoad))
        {
            if (automatic) _lastAutomaticLoadKey = null;
            else SetStatus(L("Loc.Common.OperationInProgress", "다른 작업 진행 중"),
                L("Loc.Common.WaitForOperation", "현재 작업이 끝난 뒤 다시 시도하세요."), InfoBarSeverity.Warning);
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _projectionCancellation = cancellation;
        IsLoadingExistingIndex = mode == CatalogOperationMode.ExistingIndex;
        _isAutomaticIndexLoad = automatic && IsLoadingExistingIndex;
        _activeOperationMode = mode;
        IsBusy = true;
        IsScanning = mode == CatalogOperationMode.Scan;
        ProgressMessage = F("Loc.Catalog.Operation.Started", "{0} 작업을 시작했습니다.", name);
        SetStatus(name, ProgressMessage, InfoBarSeverity.Informational);
        CatalogProjectionSnapshot? published = null;
        CatalogResult? result = null;
        ICatalogProjectionWriter? writer = null;
        CatalogProjectionScope? scope = null;
        var scanFields = Fields.ToArray();
        var scanShowsOriginal = ShowOriginalValues;
        var liveRows = new ConcurrentDictionary<string, CatalogItemViewModel>(StringComparer.OrdinalIgnoreCase);
        var operationProfile = SelectedProfile?.Profile;
        var hadPublishedResults = _displayedProjectionSnapshot is not null;
        var liveVersion = Interlocked.Increment(ref _resultSessionVersion);
        ResetLiveFilters();
        var followUp = CatalogMissingDataDecision.Later;
        WorkspaceSelection? requestedWorkspace = null;
        try
        {
            var workspace = ValidateWorkspace(requireExistingRoot: false);
            requestedWorkspace = workspace;
            CancelWorkspacePersistenceDebounce();
            var save = CreateWorkspaceSaveAttempt(Interlocked.Increment(ref _workspaceDraftVersion),
                WorkspaceDraftFields.Profile | WorkspaceDraftFields.RootPath | ReadWorkspaceDirtyFields());
            Volatile.Write(ref _latestWorkspaceDraft, save);
            async Task Execute(CancellationToken token)
            {
                if (mode == CatalogOperationMode.Scan)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(workspace.RootPath))
                        throw new DirectoryNotFoundException(workspace.RootPath);
                    EnsureDatabaseDirectory(workspace.DatabasePath);
                }
                token.ThrowIfCancellationRequested();
                try { await PersistWorkspaceAsync(save, token).ConfigureAwait(false); }
                catch { AbandonWorkspaceSaveAttempt(save); throw; }
                Guid? previousScan = null;
                if (_indexSessions is not null)
                {
                    await using var index = _indexSessions.Create(workspace.DatabasePath);
                    var status = await index.GetRootStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
                    previousScan = status.LastScanId;
                    if (mode == CatalogOperationMode.ExistingIndex && status.Availability != IndexRootAvailability.Available)
                        throw new MissingProjectionException(false);
                }
                scope = CreateProjectionScope(workspace, previousScan, operationProfile!);
                if (mode == CatalogOperationMode.ExistingIndex)
                    published = await _projectionStore!.FindAsync(scope, token).ConfigureAwait(false);
                if (published is not null && published.Summary.MatchedCount > 0) return;
                if (mode == CatalogOperationMode.ExistingIndex && !allowBuildFromIndex)
                    throw new MissingProjectionException(true);
                writer = _projectionStore!.Begin(scope, scanFields);
                // Only bounded live rows are materialized. The projection writer
                // receives every match synchronously on the scan worker.
                using var liveDelivery = new CoalescingProgressDelivery<Action>(
                    action => _dispatcher.BeginInvoke(DispatcherPriority.Background, action), action => action());
                var delivery = new CatalogProgressDelivery(liveDelivery.Report, batch =>
                {
                    if (_disposed || liveVersion != Volatile.Read(ref _resultSessionVersion)) return;
                    if (!hadPublishedResults && _projectionSnapshot is not null) return;
                    ProgressMessage = FormatProgress(batch.Latest, batch.Latest.MatchedCount);
                    if (!hadPublishedResults && batch.Latest.AttemptReset) _loadedItems.Clear();
                    if (!hadPublishedResults && batch.Matches.Count > 0)
                    {
                        foreach (var value in batch.Matches.Take(Math.Max(0, 500 - _loadedItems.Count)))
                        {
                            if (!liveRows.TryRemove(value.MatchedItem!.FullPath, out var row)) continue;
                            _loadedItems.Add(row);
                        }
                        HasLoadedItems = _loadedItems.Count > 0;
                        FilterSummary = F("Loc.Catalog.Filter.Empty", "{0:N0}개 항목", batch.Latest.MatchedCount);
                    }
                    if (!hadPublishedResults && (batch.Latest.AttemptReset || batch.Matches.Count > 0)) Items.Refresh();
                    if (!hadPublishedResults) ScheduleLiveFilterRefresh();
                });
                var progress = new CatalogProgressBatcher(_indexingSettings?.Current.UiUpdateBatchSize ?? 10, batch =>
                {
                    if (batch.Latest.AttemptReset) liveRows.Clear();
                    if (!hadPublishedResults) CaptureLiveFilterSnapshot(writer, scope, scanFields, liveVersion);
                    var previews = batch.Matches.Select(value =>
                    {
                        var fullItem = value.MatchedItem!;
                        var previewFields = scanFields.Where(field => field.SourceKind == ProfileFieldSourceKind.TextFileContent &&
                            fullItem.Values.TryGetValue(field.FieldId, out var raw) && raw is string text && text.Length > 2048)
                            .Select(field => field.FieldId).ToArray();
                        if (previewFields.Length > 0)
                        {
                            var values = fullItem.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                            foreach (var field in previewFields)
                            {
                                var text = (string)values[field]!;
                                values[field] = text[..(char.IsHighSurrogate(text[2047]) ? 2047 : 2048)];
                            }
                            fullItem = fullItem with { Model = values, Values = values };
                        }
                        if (!hadPublishedResults)
                        {
                            var row = CreateItemViewModel(fullItem, scanFields, workspace.ProfileId, scanShowsOriginal);
                            row.PreviewFields = previewFields;
                            liveRows[fullItem.FullPath] = row;
                        }
                        return value with { MatchedItem = fullItem, PreviewFields = previewFields };
                    }).ToArray();
                    var processedCount = Math.Max(batch.Latest.MatchedCount, writer.Count);
                    delivery.Post(new CatalogProgressBatch(
                        batch.Latest with { MatchedItem = null, MatchedCount = checked((int)processedCount) }, previews));
                });
                var request = CreateRequest(workspace) with { RetainItems = false, ResultWriter = writer };
                result = mode == CatalogOperationMode.Scan
                    ? await _catalogService.ScanAndLoadAsync(request, progress, token).ConfigureAwait(false)
                    : await _catalogService.LoadExistingAsync(request, progress, token).ConfigureAwait(false);
                progress.FlushAndClose();
                token.ThrowIfCancellationRequested();
                var finalScope = scope with { IndexScanId = result.ScanReport?.ScanId ?? previousScan };
                published = writer.Publish(new(result.CandidateCount, result.MatchedCount,
                    result.InvalidCount, result.NoMatchCount, result.ExcludedDirectoryCount,
                    result.HasPendingScopes || result.ScanReport?.Errors.Count > 0 ||
                    result.DirectoryExclusionIssueCount > 0,
                    result.ScanReport?.Progress.Elapsed, DateTimeOffset.UtcNow) { FieldIssueRowCount = result.FieldIssueRowCount }, finalScope);
            }
            try
            {
                if (mode == CatalogOperationMode.Scan)
                    await _operationCoordinator.ReplaceAsync(ApplicationOperationKind.IndexLoad,
                        ApplicationOperationKind.IndexWrite, Execute, cancellation.Token);
                else
                    await _operationCoordinator.RunAsync(ApplicationOperationKind.IndexLoad, Execute, cancellation.Token);
            }
            catch { AbandonWorkspaceSaveAttempt(save); throw; }
            cancellation.Token.ThrowIfCancellationRequested();
            await CommitTrackedNotesAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            _projectionSnapshot = published ?? throw new InvalidOperationException("No catalog projection was published.");
            Fields = _projectionSnapshot.Fields;
            HasValueMappings = Fields.Any(field => field.HasValueMappings);
            await RefreshProjectionQueryAsync(force: true, cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_projectionQuery?.Identity != (_projectionSnapshot.Revision == 0 ? _projectionSnapshot.Generation
                : _projectionSnapshot.Generation + ":" + _projectionSnapshot.Revision))
                throw new InvalidOperationException(L("Loc.Catalog.Query.DisplayFailed", "저장 결과를 화면에 불러오지 못했습니다. 다시 불러오세요."));
            OnPropertyChanged(nameof(ResultSourceDescription));
            var summary = _projectionSnapshot.Summary;
            var incomplete = summary.HasPendingScopes || summary.InvalidCount > 0 || summary.FieldIssueRowCount > 0;
            var message = F("Loc.Catalog.Query.Summary",
                "후보 {0:N0} · 일치 {1:N0} · 규칙 외 {2:N0} · 변환 오류 {3:N0} · 제외 {4:N0}",
                summary.CandidateCount, summary.MatchedCount, summary.NoMatchCount,
                summary.InvalidCount, summary.ExcludedCount);
            if (summary.FieldIssueRowCount > 0)
                message += F("Loc.Catalog.Query.FieldIssues", " · 컬럼 확인 필요 {0:N0}개 항목", summary.FieldIssueRowCount);
            if (summary.ScanElapsed is { } elapsed)
                message += mode == CatalogOperationMode.Scan
                    ? F("Loc.Catalog.Result.ElapsedSuffix", " · 소요 {0}", FormatElapsed(elapsed))
                    : F("Loc.Catalog.Result.LastScanElapsedSuffix", " · 마지막 스캔 {0}", FormatElapsed(elapsed));
            var title = mode == CatalogOperationMode.Scan
                ? L("Loc.Catalog.Result.ScanCompleted", "스캔 및 DB 저장 완료")
                : L("Loc.Catalog.Result.ExistingLoadCompleted", "기존 인덱스 불러오기 완료");
            SetStatus(title, message, incomplete ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            ProgressMessage = F("Loc.Catalog.Progress.Loaded", "{0:N0}개 항목을 불러왔습니다.", summary.MatchedCount);
            if (mode == CatalogOperationMode.Scan)
            {
                var notice = new ScanCompletionNotice(title, message, incomplete);
                _scanCompletionNotifier.Notify(notice);
                ShowSnackbar(title, message, incomplete ? ControlAppearance.Caution : ControlAppearance.Success);
            }
            else ShowSnackbar(title, message, incomplete ? ControlAppearance.Caution : ControlAppearance.Success);
        }
        catch (MissingProjectionException missing)
        {
            ProgressMessage = L("Loc.Catalog.Missing.None", "저장된 구조화 결과가 없습니다.");
            SetStatus(L("Loc.Common.Ready", "준비"), ProgressMessage, InfoBarSeverity.Informational);
            if (_missingDataPrompt is not null && !cancellation.IsCancellationRequested && (_isActive || !automatic))
            {
                try
                {
                    followUp = await _dispatcher.InvokeAsync(() => _missingDataPrompt.ChooseAsync(
                        operationProfile!.Descriptor.DisplayName, missing.HasIndex, cancellation.Token)).Task.Unwrap()
                        .WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not ask whether to create catalog results.");
                    SetStatus(L("Loc.Catalog.Status.Error", "오류"), exception.Message, InfoBarSeverity.Error);
                    ShowSnackbar(StatusTitle, StatusMessage, ControlAppearance.Danger);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!hadPublishedResults && _projectionQuery is null)
            {
                _loadedItems.Clear();
                Items.Refresh();
                HasLoadedItems = false;
                SetSelection([]);
            }
            SetStatus(L("Loc.Common.Cancelled", "취소됨"),
                L("Loc.Catalog.Query.Cancelled", "작업을 취소했습니다. 이전 저장 결과는 유지됩니다."), InfoBarSeverity.Warning);
            ProgressMessage = L("Loc.Common.Cancelled", "취소됨");
            ShowSnackbar(L("Loc.Common.Cancelled", "취소됨"), StatusMessage, ControlAppearance.Caution);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Catalog projection operation failed.");
            if (!hadPublishedResults && _projectionQuery is null)
            {
                _loadedItems.Clear();
                Items.Refresh();
                HasLoadedItems = false;
                SetSelection([]);
            }
            if (automatic && requestedWorkspace is { } failed && MatchesRequestedWorkspace(failed))
                _automaticLoadSuppressed = true;
            SetStatus(L("Loc.Catalog.Status.Error", "오류"),
                UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception), InfoBarSeverity.Error);
            ProgressMessage = L("Loc.Catalog.Progress.Failed", "작업 실패");
            ShowSnackbar(StatusTitle, StatusMessage, ControlAppearance.Danger);
        }
        finally
        {
            // Queued callbacks must not touch the disposed staging writer.
            Interlocked.Increment(ref _resultSessionVersion);
            ResetLiveFilters();
            try
            {
                // Abandoned stages cascade-delete persisted rows/cells and can
                // wait for the write gate. Cleanup must not run on the UI thread.
                if (writer is not null) await Task.Run(writer.Dispose);
            }
            catch (Exception cleanupFailure)
            {
                _logger.LogWarning(cleanupFailure, "Could not discard an unpublished catalog stage.");
            }
            finally
            {
                _projectionCancellation = null;
                IsScanning = false;
                IsLoadingExistingIndex = false;
                _isAutomaticIndexLoad = false;
                IsBusy = false;
                _activeOperationMode = default;
                ApplyPendingProfileSnapshot();
                if (automatic && _isActive) ScheduleAutomaticLoad();
            }
        }
        if (followUp != CatalogMissingDataDecision.Later && !cancellation.IsCancellationRequested
            && !_disposed && requestedWorkspace is { } requested
            && ReferenceEquals(SelectedProfile?.Profile, operationProfile)
            && MatchesRequestedWorkspace(requested))
        {
            if (followUp == CatalogMissingDataDecision.Scan) await ScanAsync();
            else await RunProjectionOperationAsync(name, CatalogOperationMode.ExistingIndex, false,
                cancellationToken, allowBuildFromIndex: true);
        }
    }

    private sealed class MissingProjectionException(bool hasIndex) : Exception
    { public bool HasIndex { get; } = hasIndex; }

    private bool MatchesRequestedWorkspace(WorkspaceSelection requested)
    {
        try { return ValidateWorkspace(requireExistingRoot: false) == requested; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or NotSupportedException or PathTooLongException) { return false; }
    }

    private async Task RefreshProjectionQueryAsync(bool preserveSelection = false, bool force = false,
        CancellationToken cancellationToken = default, bool allowRevisionChange = false, bool debounce = false)
    {
        if (ActiveProjectionStore is null || _projectionSnapshot is not { } snapshot) return;
        if (_activeNoteEdits > 0 && !force)
        {
            _deferredPreserveSelection = _deferredQueryRefresh
                ? _deferredPreserveSelection && preserveSelection : preserveSelection;
            _deferredQueryRefresh = true;
            return;
        }
        _deferredQueryRefresh = false;
        _queryCancellation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _queryCancellation = source;
        var version = Interlocked.Increment(ref _queryVersion);
        var definition = _queryDefinition with { Keyword = FilterText, ShowOriginalValues = ShowOriginalValues };
        _queryDefinition = definition;
        IResultQuerySession<CatalogProjectionRow>? query = null;
        try
        {
            // Debounce query construction, not the user's text input. Superseded
            // definitions never allocate a grid or queue a count query.
            if (debounce) await Task.Delay(250, source.Token);
            query = await OpenProjectionQueryAsync(snapshot, definition, source.Token);
            var localizer = _localizer;
            var loader = new ResultPageLoader<CatalogProjectionRow, CatalogItemViewModel>(query,
                row => CreateProjectionItem(row, snapshot, definition, localizer));
            var first = await loader.ReadAsync(0, VirtualResultCollection<CatalogItemViewModel>.PageSize, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _queryVersion) || _disposed) return;
            preserveSelection = preserveSelection && _displayedProjectionSnapshot?.Generation == snapshot.Generation &&
                (allowRevisionChange || _displayedProjectionSnapshot.Revision == snapshot.Revision) &&
                _displayedQueryDefinition.HasSamePredicates(definition);
            ReleaseProjectionQuery(cancelPending: false);
            _projectionQuery = query;
            _displayedProjectionSnapshot = snapshot;
            _displayedQueryDefinition = definition;
            query = null;
            var adoptedQuery = _projectionQuery;
            if (!preserveSelection) { _querySelection.Clear(); _selectionAnchor = -1; }
            _matchingCount = preserveSelection ? _matchingCount : first.Items.Count;
            _hasExactResultCount = !first.HasMore;
            IsResultCountPending = true;
            _virtualItems = new VirtualResultCollection<CatalogItemViewModel>(
                loader.ReadAsync,
                _ => new CatalogItemViewModel(new CatalogItem("", "", "", new object(),
                    new Dictionary<string, object?>(), false), snapshot.Fields, _localizer) { IsPlaceholder = true },
                action => _dispatcher.BeginInvoke(DispatcherPriority.Background, action),
                exception => { if (ReferenceEquals(adoptedQuery, _projectionQuery)) _ = RecoverProjectionAsync(snapshot, exception); });
            _virtualItems.PageLoaded += (sender, page) =>
            {
                if (!ReferenceEquals(sender, _virtualItems) || !ReferenceEquals(adoptedQuery, _projectionQuery)) return;
                foreach (var item in page.Items) TrackNoteEditors(item, snapshot.Scope.ProfileId);
                UpdateProjectionSelection(page.Items);
            };
            _virtualItems.Seed(first);
            Items = new ListCollectionView(_virtualItems);
            AppliedHighlightQuery = definition.Keyword ?? string.Empty;
            _loadedItems.Clear();
            SelectedItem = null;
            HasLoadedItems = snapshot.Summary.MatchedCount > 0;
            FilterSummary = IsResultCountPending
                ? F("Loc.Files.Results.CountPending", "{0:N0}개 로드됨 · 전체 개수 확인 중", first.Items.Count)
                : F("Loc.Catalog.Query.FilterSummary", "{0:N0} / {1:N0}개 항목", first.Items.Count, snapshot.Summary.MatchedCount);
            OnPropertyChanged(nameof(UsesQuerySelection));
            NotifySelectionImportAvailability();
            RefreshTextColumnCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ResultSourceDescription));
            UpdateProjectionSelection();
            PruneCompletedNoteEditors();
            // Counting a substring filter can inspect every row. The first page
            // is already usable while this work finishes on the query worker.
            var total = await Task.Run(() => adoptedQuery.CountAsync(source.Token), source.Token).WaitAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _queryVersion) || _disposed || !ReferenceEquals(adoptedQuery, _projectionQuery)) return;
            _matchingCount = total;
            _hasExactResultCount = true;
            _virtualItems.SetCount(total);
            IsResultCountPending = false;
            FilterSummary = F("Loc.Catalog.Query.FilterSummary", "{0:N0} / {1:N0}개 항목", total, snapshot.Summary.MatchedCount);
            UpdateProjectionSelection();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); }
        catch (Exception exception)
        {
            if (version != Volatile.Read(ref _queryVersion) || source.IsCancellationRequested || _disposed) return;
            _logger.LogWarning(exception, "Could not refresh catalog query.");
            SetStatus(L("Loc.Catalog.Status.Error", "오류"), exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            if (query is not null) _ = DisposeProjectionQueryAsync(query);
            if (ReferenceEquals(_queryCancellation, source))
            {
                _queryCancellation = null;
                IsResultCountPending = false;
                if (!_hasExactResultCount && _virtualItems is { } partial)
                    FilterSummary = F("Loc.Catalog.Query.CountIncomplete",
                        "{0:N0}개 불러옴 · 전체 개수 확인이 중지되었습니다. 불러오기로 다시 확인할 수 있습니다.", partial.CachedItemCount);
            }
            source.Dispose();
        }
    }

    private async Task<IResultQuerySession<CatalogProjectionRow>> OpenProjectionQueryAsync(
        CatalogProjectionSnapshot snapshot, CatalogProjectionQuery definition, CancellationToken token)
    {
        var store = ActiveProjectionStore ?? throw new InvalidOperationException("No catalog data source is available.");
        var opening = Task.Run(() => store.OpenQuery(snapshot, definition), token);
        try { return await opening.WaitAsync(token); }
        catch
        {
            // A synchronous provider may finish after UI cancellation. Dispose
            // its late session instead of leaking a generation lease.
            _ = opening.ContinueWith(async completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                    await DisposeProjectionQueryAsync(completed.Result);
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    private CatalogItemViewModel CreateProjectionItem(CatalogProjectionRow row, CatalogProjectionSnapshot snapshot,
        CatalogProjectionQuery definition, IAppLocalizer localizer)
    {
        var saved = snapshot.Fields.Where(field => field.SourceKind == ProfileFieldSourceKind.UserInput)
            .ToDictionary(field => field.FieldId,
                field => Convert.ToString(row.Values.GetValueOrDefault(field.FieldId), localizer.Culture) ?? "",
                StringComparer.OrdinalIgnoreCase);
        CatalogItemViewModel? item = null;
        item = new CatalogItemViewModel(row.ToItem(), snapshot.Fields, localizer, saved,
            (field, value) => PersistTrackedNoteAsync(snapshot.Scope.ProfileId, row.FullPath, field, value,
                item!.UserInputValues[field]),
            () => _dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => { if (!_disposed) _ = RefreshProjectionQueryAsync(); })))
        { FieldErrors = row.FieldErrors ?? new Dictionary<string, string>(), PreviewFields = row.PreviewFields,
          SourceFullPath = row.OriginalFullPath, SourceRowKey = row.StableKey };
        item.SetShowOriginalValues(definition.ShowOriginalValues);
        return item;
    }

    private void ReleaseProjectionQuery(bool cancelPending = true)
    {
        CancelSelectionRequests();
        if (cancelPending) { _queryCancellation?.Cancel(); Interlocked.Increment(ref _queryVersion); }
        _virtualItems?.Dispose();
        _virtualItems = null;
        if (_projectionQuery is { } query) _ = DisposeProjectionQueryAsync(query);
        _projectionQuery = null;
        OnPropertyChanged(nameof(UsesQuerySelection));
        NotifySelectionImportAvailability();
        RefreshTextColumnCommand.NotifyCanExecuteChanged();
    }

    private async Task DisposeProjectionQueryAsync(IResultQuerySession<CatalogProjectionRow> query)
    {
        try { await query.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogDebug(exception, "Could not prune a released projection query."); }
    }

    private async Task RecoverProjectionAsync(CatalogProjectionSnapshot snapshot, Exception exception)
    {
        if (_disposed || _isRecoveringProjection || IsBusy) return;
        if (_activeNoteEdits > 0)
        {
            _deferredQueryRefresh = true;
            _deferredPreserveSelection = false;
            return;
        }
        if (exception is not ResultQueryChangedException)
        {
            SetStatus(L("Loc.Catalog.Status.Error", "오류"), exception.Message, InfoBarSeverity.Warning);
            return;
        }
        _isRecoveringProjection = true;
        try
        {
            var store = ActiveProjectionStore;
            if (store is null) return;
            var latest = await Task.Run(() => store.FindAsync(snapshot.Scope));
            if (_disposed || _displayedProjectionSnapshot?.Generation != snapshot.Generation) return;
            if (latest is not null)
            {
                _projectionSnapshot = latest;
                await RefreshProjectionQueryAsync();
            }
            else
            {
                ReleaseProjectionQuery();
                _loadedItems.Clear();
                Items = CreateItemsView(_loadedItems);
                HasLoadedItems = false;
                _querySelection.Clear();
                SelectedCount = 0;
                SetStatus(L("Loc.Catalog.Status.Error", "오류"),
                    UserFacingExceptionLocalizer.TranslateOperationFailure(_localizer, exception), InfoBarSeverity.Warning);
            }
        }
        catch (Exception failure)
        {
            if (!_disposed) SetStatus(L("Loc.Catalog.Status.Error", "오류"), failure.Message, InfoBarSeverity.Warning);
        }
        finally { _isRecoveringProjection = false; }
    }

    public void ApplySort(string fieldId, EntrySortDirection direction)
    {
        if (_projectionSnapshot is null) return;
        if (fieldId.StartsWith("DisplayValues[", StringComparison.Ordinal))
            fieldId = fieldId["DisplayValues[".Length..].TrimEnd(']');
        if (fieldId.StartsWith("UserInputValues[", StringComparison.Ordinal))
            fieldId = fieldId["UserInputValues[".Length..].Split(']')[0];
        if (fieldId == "CoverageText") fieldId = "CoveragePending";
        var target = fieldId == "FullPath" ? CatalogColumnTarget.FullPath : fieldId == "CoveragePending"
            ? CatalogColumnTarget.Coverage : CatalogColumnTarget.Field(fieldId);
        ApplyColumnSort(target, direction == EntrySortDirection.Descending);
    }

    private bool CanSelectAllResults() => !UsesQuerySelection || _hasExactResultCount;
    [RelayCommand(CanExecute = nameof(CanSelectAllResults))] private void SelectAll()
    {
        CancelSelectionRequests();
        if (!CanSelectAllResults()) return;
        if (UsesQuerySelection) { _querySelection.SelectAll(); UpdateProjectionSelection(); }
        else SetSelection(Items.Cast<CatalogItemViewModel>());
    }
    [RelayCommand] private void ClearSelection()
    {
        CancelSelectionRequests();
        _querySelection.Clear(); _selectionAnchor = -1;
        if (UsesQuerySelection) UpdateProjectionSelection(); else SetSelection([]);
    }
    [RelayCommand(CanExecute = nameof(CanSelectAllResults))] private void InvertSelection()
    {
        CancelSelectionRequests();
        if (!CanSelectAllResults()) return;
        if (UsesQuerySelection) { _querySelection.Invert(); UpdateProjectionSelection(); }
        else SetSelection(Items.Cast<CatalogItemViewModel>().Except(_selectedItems).ToArray());
    }
    public async Task SelectRowAsync(object value, bool additive, bool range)
    {
        try
        {
            if (value is not CatalogItemViewModel { IsPlaceholder: false } row) return;
            if (_projectionQuery is not { } query)
            {
                var visibleItems = Items.Cast<CatalogItemViewModel>().ToArray();
                var visibleIndex = Array.IndexOf(visibleItems, row);
                if (visibleIndex < 0) return;
                var selected = additive ? _selectedItems.ToHashSet() : [];
                if (range && _selectionAnchor >= 0 && _selectionAnchor < visibleItems.Length)
                {
                    for (var rangeIndex = Math.Min(visibleIndex, _selectionAnchor);
                         rangeIndex <= Math.Max(visibleIndex, _selectionAnchor); rangeIndex++)
                        selected.Add(visibleItems[rangeIndex]);
                }
                else
                {
                    if (!additive || !selected.Remove(row)) selected.Add(row);
                    _selectionAnchor = visibleIndex;
                }
                SelectedItem = row;
                SetSelection(selected);
                return;
            }
            var index = _virtualItems!.IndexOf(row);
            _selectionRevision++;
            if (!await SelectionController.SelectAsync(query, row.SelectionKey, index, _selectionAnchor,
                additive, range, item => item.StableKey ?? item.FullPath,
                () => !_disposed && ReferenceEquals(query, _projectionQuery))) return;
            _selectionRevision++;
            if (!range || _selectionAnchor < 0) _selectionAnchor = index;
            SelectedItem = row;
            UpdateProjectionSelection(additive && !range && _querySelection.Baseline is null ? [row] : null);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or ResultQueryChangedException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Selection query changed before the range was selected.");
        }
    }
    private void UpdateProjectionSelection(IReadOnlyList<CatalogItemViewModel>? changedItems = null)
    {
        if (_virtualItems is null) return;
        var items = changedItems ?? _virtualItems.LoadedItems;
        if (_querySelection.Baseline is null)
            foreach (var item in items) item.IsSelected = _querySelection.Contains(item.SelectionKey);
        else _ = UpdateProjectionSelectionFlagsAsync(items, _selectionRevision, _projectionQuery);
        SelectedCount = checked((int)_querySelection.Count(_matchingCount));
        CopySelectionCommand.NotifyCanExecuteChanged();
        ExportSelectionCommand.NotifyCanExecuteChanged();
    }

    private void CancelSelectionRequests()
    {
        _selectionRevision++;
        _selectionController?.Cancel();
    }

    private async Task UpdateProjectionSelectionFlagsAsync(IReadOnlyList<CatalogItemViewModel> items,
        long revision, IResultQuerySession<CatalogProjectionRow>? query)
    {
        using var captured = _querySelection.Capture();
        try
        {
            var flags = await captured.ReadMembershipAsync(items.Select(item => item.SelectionKey).ToArray(), _outputLifetime.Token);
            if (_disposed || revision != _selectionRevision || !ReferenceEquals(query, _projectionQuery)) return;
            for (var index = 0; index < items.Count; index++) items[index].IsSelected = flags[index];
        }
        catch (OperationCanceledException) when (_outputLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not refresh selection flags."); }
    }

    private bool CanRefreshTextColumn(string fieldId) => !IsBusy && !IsReadOnlyDataSource && _projectionQuery is not null &&
        Fields.Any(field => field.FieldId == fieldId && field.SourceKind == ProfileFieldSourceKind.TextFileContent);

    [RelayCommand(CanExecute = nameof(CanRefreshTextColumn))]
    private async Task RefreshTextColumnAsync(string fieldId)
    {
        if (IsBusy || IsReadOnlyDataSource || _projectionSnapshot is not { } snapshot || _projectionStore is null ||
            SelectedProfile?.Profile is not { } profile) return;
        if (!snapshot.Fields.Any(field => field.FieldId == fieldId && field.SourceKind == ProfileFieldSourceKind.TextFileContent)) return;
        using var source = new CancellationTokenSource();
        _projectionCancellation = source;
        IsBusy = true;
        try
        {
            await CommitTrackedNotesAsync();
            CatalogProjectionSnapshot? updated = null;
            await _operationCoordinator.RunAsync(ApplicationOperationKind.IndexWrite, async token =>
            {
                updated = await _projectionStore.RefreshColumnAsync(snapshot, fieldId, (path, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var read = profile.ReadTextColumn(path, fieldId);
                    return Task.FromResult(new TextColumnReadResult(read.Value,
                        read.Issues.Select(issue => issue with
                        {
                            Message = ProfileDiagnosticLocalizer.Translate(_localizer, issue),
                        }).ToArray()) { Resource = read.Resource });
                }, new Progress<TextColumnRefreshProgress>(progress => _dispatcher.BeginInvoke(new Action(() =>
                    ProgressMessage = F("Loc.Catalog.TextRefresh.Progress", "{0:N0} / {1:N0} · 오류 {2:N0}",
                        progress.Completed, progress.Total, progress.Errors)))), token).ConfigureAwait(false);
            }, source.Token);
            _projectionSnapshot = updated!;
            await CommitTrackedNotesAsync();
            await RefreshProjectionQueryAsync(force: true);
            OnPropertyChanged(nameof(ResultSourceDescription));
            SetStatus(L("Loc.Catalog.TextRefresh.Completed", "텍스트 컬럼 새로고침 완료"),
                L("Loc.Catalog.TextRefresh.Scope", "필터와 선택에 관계없이 현재 프로필 결과 전체의 해당 컬럼만 다시 읽었습니다."),
                InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) { SetStatus(L("Loc.Common.Cancelled", "취소됨"),
            L("Loc.Catalog.TextRefresh.Cancelled", "취소했습니다. 이전 컬럼 값은 유지됩니다."), InfoBarSeverity.Warning); }
        catch (Exception exception) { SetStatus(L("Loc.Catalog.Status.Error", "오류"), exception.Message, InfoBarSeverity.Error); }
        finally { _projectionCancellation = null; IsBusy = false; }
    }

    private async Task CopyQuerySelectionAsync()
    {
        var token = _outputLifetime.Token;
        try
        {
            var snapshot = _displayedProjectionSnapshot!;
            using var selection = _querySelection.Capture();
            var definition = _displayedQueryDefinition with { TextPreviewCharacters = 0 };
            var copiedCount = SelectedCount;
            var format = SelectedOutputFormat ?? SelectionOutputFormatDefaults.CreateFullPathLines(_localizer);
            var importedSource = WorkspaceTransfer?.CurrentSource;
            var liveStore = _projectionStore;
            await CommitTrackedNotesAsync();
            using var ownedStore = importedSource is not null
                ? await Task.Run(() => _datasetTransfer!.OpenCatalogStore(importedSource) as IDisposable, token) : null;
            var store = ownedStore as ICatalogProjectionStore ?? liveStore ?? throw new InvalidOperationException("No catalog data source is available.");
            await using var query = await Task.Run(() => store.OpenQuery(snapshot, definition), token);
            async IAsyncEnumerable<SelectionOutputItem> Outputs([EnumeratorCancellation] CancellationToken token = default)
            {
                await foreach (var row in query.EnumerateSelectionAsync(selection, token))
                {
                    var item = CreateProjectionItem(row, snapshot, definition, _localizer);
                    yield return new(row.FullPath, row.FullPath,
                        Path.GetFileName(Path.TrimEndingDirectorySeparator(row.FullPath)), item.OutputValues)
                    { OriginalFullPath = row.OriginalFullPath };
                }
            }
            var text = await Task.Run(() => _outputFormatter.FormatAsync(format, Outputs(token), token), token);
            token.ThrowIfCancellationRequested();
            if (!_clipboardService.TrySetText(text, out var error)) throw new InvalidOperationException(error);
            ShowSnackbar(L("Loc.Selection.CopyCompleted", "선택 항목 복사 완료"),
                F("Loc.Selection.CopyCompleted.Message", "{0:N0}개 항목을 '{1}' 형식으로 복사했습니다.", copiedCount, format.DisplayName),
                ControlAppearance.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (!_disposed) SetStatus(L("Loc.Selection.CopyFailed", "선택 항목 복사 실패"), exception.Message, InfoBarSeverity.Error); }
    }
    private async Task ExportQuerySelectionAsync()
    {
        var token = _outputLifetime.Token;
        var snapshot = _displayedProjectionSnapshot!;
        var definition = _displayedQueryDefinition with { TextPreviewCharacters = 0 };
        using var selection = _querySelection.Capture();
        var count = SelectedCount;
        var importedSource = WorkspaceTransfer?.CurrentSource;
        var liveStore = _projectionStore;
        var destination = _pickerService.PickCatalogExportPath($"FindEverything-catalog-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");
        if (destination is null) return;
        try
        {
            var fields = snapshot.Fields.OrderBy(field => field.Order).ToArray();
            await CommitTrackedNotesAsync();
            using var ownedStore = importedSource is not null
                ? await Task.Run(() => _datasetTransfer!.OpenCatalogStore(importedSource) as IDisposable, token) : null;
            var store = ownedStore as ICatalogProjectionStore ?? liveStore ?? throw new InvalidOperationException("No catalog data source is available.");
            await using var query = await Task.Run(() => store.OpenQuery(snapshot, definition), token);
            var headers = new[] { L("Loc.Catalog.Column.Status", "상태") }
                .Concat(fields.Select(field => field.Header)).Append(L("Loc.Catalog.Column.FolderPath", "폴더 경로")).ToArray();
            IEnumerable<IReadOnlyList<string>> Rows()
            {
                // The synchronous file writer pulls a bounded async query on its
                // worker. No complete row or view-model snapshot is allocated.
                var enumerator = query.EnumerateSelectionAsync(selection, token).GetAsyncEnumerator(token);
                try
                {
                    while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        var item = CreateProjectionItem(enumerator.Current, snapshot, definition, _localizer);
                        yield return CreateExportRow(item, fields);
                    }
                }
                finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            await Task.Run(() => _selectionExporter.ExportAsync(destination.Path, destination.Format, new(headers, Rows(), count), token), token);
            token.ThrowIfCancellationRequested();
            ShowSnackbar(L("Loc.Catalog.Export.Completed", "선택 항목 내보내기 완료"),
                F("Loc.Catalog.Export.Completed.Message", "{0:N0}개 항목을 저장했습니다: {1}", count, destination.Path), ControlAppearance.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (!_disposed) SetStatus(L("Loc.Catalog.Export.Failed", "선택 항목 내보내기 실패"), exception.Message, InfoBarSeverity.Error); }
    }
}
