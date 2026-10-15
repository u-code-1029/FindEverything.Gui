using System.Diagnostics;
using FindEverything.Application.Catalog;
using FindEverything.Application.Results;
using FindEverything.Desktop.Results;
using FindEverything.Desktop.Filtering;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private sealed record LiveProjectionState(CatalogProjectionSnapshot Snapshot, long SessionVersion);
    private LiveProjectionState? _liveProjectionState;
    private long _liveLastFlushTimestamp;
    private CancellationTokenSource? _liveFilterCancellation;
    private IReadOnlySet<string>? _liveMatchingPaths;
    private LiveProjectionState? _liveAppliedState;
    private bool _liveRefreshPending;
    private bool _liveRefreshQueued;
    private long _liveFilterVersion;
    private long _liveSnapshotFilterVersion = -1;
    private long _livePreviewVersion;
    private long _liveAppliedPreviewVersion = -1;
    private long _liveAppliedFilterVersion = -1;
    private readonly LatestRequestController _liveInputRequests = new();
    private IReadOnlyList<string> _liveFilterTerms = [];

    private void ScheduleTextFilter()
    {
        if (IsScanning) return;
        if (_projectionSnapshot is not null && ActiveProjectionStore is not null)
        {
            _ = RefreshProjectionQueryAsync(debounce: true);
            return;
        }
        _ = _liveInputRequests.RunAsync(SearchInputTiming.DebounceDelay, token =>
        {
            if (!_disposed) ApplyFilter();
            return Task.CompletedTask;
        });
    }

    /// <summary>Called only by the scan worker, never by a UI query. Flushing is
    /// coalesced so full-text filter eligibility can be read without retaining text.</summary>
    private bool CaptureLiveFilterSnapshot(ICatalogProjectionWriter writer, CatalogProjectionScope scope,
        IReadOnlyList<ProfileFieldDescriptor> fields, long sessionVersion)
    {
        if (_disposed || sessionVersion != Volatile.Read(ref _resultSessionVersion) || writer.Count == 0) return false;
        var filterVersion = Volatile.Read(ref _liveFilterVersion);
        if (_queryDefinition.Filters.Count == 0 && string.IsNullOrWhiteSpace(AppliedHighlightQuery))
        {
            Volatile.Write(ref _liveSnapshotFilterVersion, filterVersion);
            return true;
        }
        var existing = Volatile.Read(ref _liveProjectionState);
        var now = Stopwatch.GetTimestamp();
        if (existing?.Snapshot.Generation == writer.Generation && existing.SessionVersion == sessionVersion
            && Stopwatch.GetElapsedTime(_liveLastFlushTimestamp, now) < TimeSpan.FromMilliseconds(250)) return false;
        writer.Flush();
        _liveLastFlushTimestamp = now;
        var snapshot = new CatalogProjectionSnapshot(writer.Generation, scope,
            new(writer.Count, writer.Count, 0, 0, 0, true, null, DateTimeOffset.UtcNow), fields)
        { IsBuilding = true, BuildingRevision = writer.Revision };
        Volatile.Write(ref _liveProjectionState, new(snapshot, sessionVersion));
        Volatile.Write(ref _liveSnapshotFilterVersion, filterVersion);
        return true;
    }

    private bool MatchesLiveItem(CatalogItemViewModel item)
    {
        var live = Volatile.Read(ref _liveProjectionState);
        if (_liveMatchingPaths is not null && SameLiveGeneration(live, _liveAppliedState))
            return _liveMatchingPaths.Contains(item.FullPath);
        return item.MatchesTerms(_liveFilterTerms) && MatchesColumnFilters(item);
    }

    private static bool SameLiveRevision(LiveProjectionState? left, LiveProjectionState? right) =>
        SameLiveGeneration(left, right) && left!.Snapshot.BuildingRevision == right!.Snapshot.BuildingRevision
        && left.Snapshot.Summary.MatchedCount == right.Snapshot.Summary.MatchedCount;
    private static bool SameLiveGeneration(LiveProjectionState? left, LiveProjectionState? right) =>
        left is not null && right is not null && left.SessionVersion == right.SessionVersion
        && left.Snapshot.Generation == right.Snapshot.Generation;

    private void ScheduleLiveFilterRefresh(bool criteriaChanged = false)
    {
        if (_disposed || _projectionSnapshot is not null || _projectionStore is null) return;
        if (_queryDefinition.Filters.Count == 0 && string.IsNullOrWhiteSpace(AppliedHighlightQuery))
        {
            if (!criteriaChanged && _liveFilterCancellation is null && _liveMatchingPaths is null && _liveAppliedState is null) return;
            _liveFilterCancellation?.Cancel();
            _liveFilterCancellation = null; _liveRefreshPending = false; _liveRefreshQueued = false;
            _liveMatchingPaths = null; _liveAppliedState = null; Interlocked.Increment(ref _liveFilterVersion);
            return;
        }
        if (criteriaChanged)
        {
            _liveFilterCancellation?.Cancel();
            _liveRefreshPending = false; _liveRefreshQueued = false;
            Interlocked.Increment(ref _liveFilterVersion);
        }
        if (_activeNoteEdits > 0) { _liveRefreshQueued = true; return; }
        if (_liveRefreshPending) { _liveRefreshQueued = true; return; }
        var live = Volatile.Read(ref _liveProjectionState);
        if (live is null || live.SessionVersion != Volatile.Read(ref _resultSessionVersion)) return;
        // Once the bounded preview and criteria are unchanged, later scan
        // counters cannot change its eligibility. Do not re-query/reset the grid.
        if (SameLiveRevision(live, _liveAppliedState)
            && _liveAppliedPreviewVersion == _livePreviewVersion
            && _liveAppliedFilterVersion == Volatile.Read(ref _liveFilterVersion)) return;
        _liveRefreshPending = true;
        var cancellation = new CancellationTokenSource();
        _liveFilterCancellation = cancellation;
        _ = RefreshLiveFilterAsync(live, _livePreviewVersion, cancellation, Volatile.Read(ref _liveFilterVersion));
    }

    private async Task RefreshLiveFilterAsync(LiveProjectionState live, long previewVersion,
        CancellationTokenSource cancellation, long version)
    {
        var token = cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var paths = _loadedItems.Select(item => item.FullPath).Take(500).ToArray();
            var definition = _queryDefinition with { Keyword = AppliedHighlightQuery, ShowOriginalValues = ShowOriginalValues };
            var matches = await _projectionStore!.FindMatchingPathsAsync(live.Snapshot, definition, paths, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || version != Volatile.Read(ref _liveFilterVersion) || live.SessionVersion != Volatile.Read(ref _resultSessionVersion)
                || !SameLiveRevision(live, Volatile.Read(ref _liveProjectionState))) return;
            _liveAppliedPreviewVersion = previewVersion;
            _liveAppliedFilterVersion = version;
            if (matches is null)
            {
                var hadEligibility = _liveMatchingPaths is not null;
                _liveMatchingPaths = null;
                _liveAppliedState = live; // Custom providers retain the bounded in-memory predicate.
                if (hadEligibility) Items.Refresh();
                return;
            }
            var visibilityChanged = _loadedItems.Any(item => MatchesLiveItem(item) != matches.Contains(item.FullPath));
            _liveMatchingPaths = matches; _liveAppliedState = live;
            if (visibilityChanged) Items.Refresh();
            FilterSummary = F("Loc.Catalog.Filter.Active", "{0:N0} / {1:N0}개 항목", Items.Count, _loadedItems.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ResultQueryChangedException)
        {
            // A reset can remove the old stage while no new match exists yet.
            // Do not spin retrying the stale revision; the worker supplies the next snapshot.
            var current = Volatile.Read(ref _liveProjectionState);
            if (SameLiveRevision(live, current)) Interlocked.CompareExchange(ref _liveProjectionState, null, current);
            _liveRefreshQueued = false;
        }
        catch (Exception exception)
        {
            if (!_disposed && !token.IsCancellationRequested) _logger.LogDebug(exception, "Could not refresh live catalog filter eligibility.");
        }
        finally
        {
            if (ReferenceEquals(_liveFilterCancellation, cancellation))
            {
                _liveFilterCancellation = null; _liveRefreshPending = false;
                var queued = _liveRefreshQueued; _liveRefreshQueued = false;
                if (queued && !_disposed && _projectionSnapshot is null) ScheduleLiveFilterRefresh();
            }
            cancellation.Dispose();
        }
    }

    private void ResetLiveFilters()
    {
        _liveInputRequests.Cancel();
        _liveFilterCancellation?.Cancel(); _liveFilterCancellation = null;
        Volatile.Write(ref _liveProjectionState, null);
        _liveLastFlushTimestamp = 0; _liveMatchingPaths = null; _liveAppliedState = null;
        Volatile.Write(ref _liveSnapshotFilterVersion, -1);
        _livePreviewVersion = 0; _liveAppliedPreviewVersion = -1; _liveAppliedFilterVersion = -1;
        _liveRefreshPending = false; _liveRefreshQueued = false; Interlocked.Increment(ref _liveFilterVersion);
    }
}
