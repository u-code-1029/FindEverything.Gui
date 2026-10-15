using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public enum GridColumnWidthMode { Auto, Manual }

// Missing WidthMode in old files becomes Auto: old measured widths aren't user resize intent.
public sealed record GridColumnLayout(string FieldId, int DisplayIndex, double Width,
    GridColumnWidthMode WidthMode = GridColumnWidthMode.Auto, bool IsHidden = false);
public sealed class GridLayoutChangedEventArgs(string profileId) : EventArgs
{
    public string ProfileId { get; } = profileId;
}

public interface IGridLayoutStore
{
    event EventHandler<GridLayoutChangedEventArgs>? Changed { add { } remove { } }
    long GetRevision(string profileId) => 0;
    IReadOnlyDictionary<string, GridColumnLayout> Get(string profileId);
    void Save(string profileId, IEnumerable<GridColumnLayout> columns);
    bool TrySave(string profileId, IEnumerable<GridColumnLayout> columns, long expectedRevision)
    { Save(profileId, columns); return true; }
    Task SaveAsync(string profileId, IEnumerable<GridColumnLayout> columns, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Save(profileId, columns);
        return Task.CompletedTask;
    }
    async Task<bool> TryReplaceAsync(string profileId, IReadOnlyDictionary<string, GridColumnLayout> expectedColumns,
        IEnumerable<GridColumnLayout> replacementColumns, CancellationToken cancellationToken = default)
    {
        var current = Get(profileId);
        if (current.Count != expectedColumns.Count || current.Any(pair =>
                !expectedColumns.TryGetValue(pair.Key, out var expected) || expected != pair.Value)) return false;
        await SaveAsync(profileId, replacementColumns, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

public sealed class GridLayoutStore(AppPaths paths, ILogger<GridLayoutStore> logger) : IGridLayoutStore, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Dictionary<string, Dictionary<string, GridColumnLayout>>? _layouts;
    private Dictionary<string, Dictionary<string, GridColumnLayout>> _persistedLayouts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _debounce;
    private long _revision;
    private long _writtenRevision;
    private readonly Dictionary<string, long> _profileRevisions = new(StringComparer.OrdinalIgnoreCase);
    public event EventHandler<GridLayoutChangedEventArgs>? Changed;

    public long GetRevision(string profileId)
    { lock (_sync) return _profileRevisions.GetValueOrDefault(profileId); }

    public IReadOnlyDictionary<string, GridColumnLayout> Get(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (_sync)
        {
            EnsureLoaded();
            return _layouts!.TryGetValue(profileId, out var layout)
                ? new Dictionary<string, GridColumnLayout>(layout, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GridColumnLayout>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(string profileId, IEnumerable<GridColumnLayout> columns)
        => SaveCore(profileId, columns, null);

    public bool TrySave(string profileId, IEnumerable<GridColumnLayout> columns, long expectedRevision)
        => SaveCore(profileId, columns, expectedRevision);

    private bool SaveCore(string profileId, IEnumerable<GridColumnLayout> columns, long? expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var snapshot = Normalize(columns);
        lock (_sync)
        {
            EnsureLoaded();
            if (expectedRevision is not null && expectedRevision != _profileRevisions.GetValueOrDefault(profileId)) return false;
            _layouts![profileId] = snapshot;
            _profileRevisions[profileId] = _profileRevisions.GetValueOrDefault(profileId) + 1;
            _revision++;
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            _ = WriteDebouncedAsync(_debounce.Token);
            return true;
        }
    }

    /// <summary>A durable write for import transactions. Failures propagate and the cache changes only after commit.</summary>
    public async Task SaveAsync(string profileId, IEnumerable<GridColumnLayout> columns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await WriteAsync(profileId, null, Normalize(columns), cancellationToken).ConfigureAwait(false);
        RaiseChanged(profileId);
    }

    public async Task<bool> TryReplaceAsync(string profileId, IReadOnlyDictionary<string, GridColumnLayout> expectedColumns,
        IEnumerable<GridColumnLayout> replacementColumns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(expectedColumns);
        var replaced = await WriteAsync(profileId, new Dictionary<string, GridColumnLayout>(expectedColumns, StringComparer.OrdinalIgnoreCase),
            Normalize(replacementColumns), cancellationToken).ConfigureAwait(false);
        if (replaced) RaiseChanged(profileId);
        return replaced;
    }

    private void RaiseChanged(string profileId)
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler<GridLayoutChangedEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, new(profileId)); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not refresh imported grid layout for {ProfileId}.", profileId); }
    }

    private async Task WriteDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token).ConfigureAwait(false);
            await FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        try { await WriteAsync(null, null, null, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { logger.LogWarning(exception, "Could not save grid layout file {File}.", paths.GridLayoutFile); }
    }

    private async Task<bool> WriteAsync(string? profileId, IReadOnlyDictionary<string, GridColumnLayout>? expected,
        Dictionary<string, GridColumnLayout>? replacement, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    EnsureLoaded();
                    if (profileId is null && _writtenRevision == _revision) return true;
                }
                var bytes = File.Exists(paths.GridLayoutFile)
                    ? await File.ReadAllBytesAsync(paths.GridLayoutFile, cancellationToken).ConfigureAwait(false) : null;
                var persisted = Parse(bytes);
                var snapshot = Clone(persisted);
                long revision;
                lock (_sync)
                {
                    revision = _revision;
                    // Merge only pending local edits, retaining unrelated changes from another writer.
                    foreach (var (id, columns) in _layouts!)
                        if (!Equal(columns, _persistedLayouts.GetValueOrDefault(id)))
                            snapshot[id] = new(columns, StringComparer.OrdinalIgnoreCase);
                    if (profileId is not null)
                    {
                        if (expected is not null && !Equal(expected, snapshot.GetValueOrDefault(profileId)))
                        {
                            _layouts = snapshot;
                            _persistedLayouts = persisted;
                            return false;
                        }
                        snapshot[profileId] = replacement!;
                    }
                }
                Directory.CreateDirectory(paths.LocalDataDirectory);
                var temporaryFile = paths.GridLayoutFile + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        // Resizes arriving during the asynchronous write must not be lost.
                        if (revision != _revision) continue;
                        var currentBytes = File.Exists(paths.GridLayoutFile) ? File.ReadAllBytes(paths.GridLayoutFile) : null;
                        if (!BytesEqual(bytes, currentBytes)) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(temporaryFile, paths.GridLayoutFile, overwrite: true);
                        _layouts = snapshot;
                        _persistedLayouts = Clone(snapshot);
                        if (profileId is not null)
                            _profileRevisions[profileId] = _profileRevisions.GetValueOrDefault(profileId) + 1;
                        _writtenRevision = ++_revision;
                        return true;
                    }
                }
                finally
                {
                    try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
                    catch (IOException exception) { logger.LogDebug(exception, "Could not remove temporary grid layout file."); }
                }
            }
            throw new IOException("The grid layout changed repeatedly while it was being saved. Please retry.");
        }
        finally { _writer.Release(); }
    }

    private void EnsureLoaded()
    {
        if (_layouts is not null) return;
        try
        {
            _layouts = Parse(File.Exists(paths.GridLayoutFile) ? File.ReadAllBytes(paths.GridLayoutFile) : null);
            _persistedLayouts = Clone(_layouts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read grid layout file {File}.", paths.GridLayoutFile);
            _layouts = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, Dictionary<string, GridColumnLayout>> Parse(byte[]? bytes)
    {
        var source = bytes is null ? [] : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, GridColumnLayout>>>(bytes, JsonOptions) ?? [];
        var result = new Dictionary<string, Dictionary<string, GridColumnLayout>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileId, columns) in source)
        {
            if (string.IsNullOrWhiteSpace(profileId) || columns is null) continue;
            var normalized = new Dictionary<string, GridColumnLayout>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fieldId, column) in columns)
                if (!string.IsNullOrWhiteSpace(fieldId) && column is not null && !string.IsNullOrWhiteSpace(column.FieldId))
                    normalized[fieldId] = column;
            result[profileId] = normalized;
        }
        return result;
    }

    private static Dictionary<string, GridColumnLayout> Normalize(IEnumerable<GridColumnLayout> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var result = new Dictionary<string, GridColumnLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            ArgumentNullException.ThrowIfNull(column);
            if (string.IsNullOrWhiteSpace(column.FieldId) || column.DisplayIndex < 0 || !double.IsFinite(column.Width)
                || column.Width < 0 || !Enum.IsDefined(column.WidthMode))
                throw new ArgumentException("Grid column values are invalid.", nameof(columns));
            result.Add(column.FieldId, column);
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, GridColumnLayout>> Clone(
        Dictionary<string, Dictionary<string, GridColumnLayout>> source) => source.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, GridColumnLayout>(pair.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    private static bool Equal(IReadOnlyDictionary<string, GridColumnLayout>? left, IReadOnlyDictionary<string, GridColumnLayout>? right) =>
        (left?.Count ?? 0) == (right?.Count ?? 0)
        && (left is null || left.All(pair => right is not null && right.TryGetValue(pair.Key, out var other) && pair.Value == other));
    private static bool BytesEqual(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() => { lock (_sync) EnsureLoaded(); }, cancellationToken);
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync) _debounce?.Cancel();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
