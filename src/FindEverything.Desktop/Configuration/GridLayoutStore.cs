using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Configuration;

public enum GridColumnWidthMode { Auto, Manual }

// Missing WidthMode in old files becomes Auto: old measured widths aren't user resize intent.
public sealed record GridColumnLayout(string FieldId, int DisplayIndex, double Width,
    GridColumnWidthMode WidthMode = GridColumnWidthMode.Auto, bool IsHidden = false);

public interface IGridLayoutStore
{
    IReadOnlyDictionary<string, GridColumnLayout> Get(string profileId);
    void Save(string profileId, IEnumerable<GridColumnLayout> columns);
}

public sealed class GridLayoutStore(AppPaths paths, ILogger<GridLayoutStore> logger) : IGridLayoutStore, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Dictionary<string, Dictionary<string, GridColumnLayout>>? _layouts;
    private CancellationTokenSource? _debounce;
    private long _revision;
    private long _writtenRevision;

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(columns);
        lock (_sync)
        {
            EnsureLoaded();
            _layouts![profileId] = columns.ToDictionary(static column => column.FieldId, StringComparer.OrdinalIgnoreCase);
            _revision++;
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            _ = WriteDebouncedAsync(_debounce.Token);
        }
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
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, Dictionary<string, GridColumnLayout>> snapshot;
            long revision;
            lock (_sync)
            {
                if (_layouts is null || _writtenRevision == _revision) return;
                revision = _revision;
                snapshot = _layouts.ToDictionary(static p => p.Key,
                    static p => new Dictionary<string, GridColumnLayout>(p.Value), StringComparer.OrdinalIgnoreCase);
            }
            Directory.CreateDirectory(paths.LocalDataDirectory);
            var temporaryFile = paths.GridLayoutFile + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryFile, paths.GridLayoutFile, overwrite: true);
                lock (_sync) _writtenRevision = revision;
            }
            finally { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Could not save grid layout file {File}.", paths.GridLayoutFile); }
        finally { _writer.Release(); }
    }

    private void EnsureLoaded()
    {
        if (_layouts is not null) return;
        try
        {
            var source = File.Exists(paths.GridLayoutFile)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, GridColumnLayout>>>(File.ReadAllText(paths.GridLayoutFile), JsonOptions) ?? [] : [];
            _layouts = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (profileId, columns) in source)
            {
                if (string.IsNullOrWhiteSpace(profileId) || columns is null) continue;
                var normalized = new Dictionary<string, GridColumnLayout>(StringComparer.OrdinalIgnoreCase);
                foreach (var (fieldId, column) in columns)
                    if (!string.IsNullOrWhiteSpace(fieldId) && column is not null && !string.IsNullOrWhiteSpace(column.FieldId))
                        normalized[fieldId] = column;
                _layouts[profileId] = normalized;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read grid layout file {File}.", paths.GridLayoutFile);
            _layouts = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() => { lock (_sync) EnsureLoaded(); }, cancellationToken);
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync) _debounce?.Cancel();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
