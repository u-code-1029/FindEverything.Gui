using System.Text.Json;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FindEverything.Desktop.Services;

/// <summary>Best-effort reclamation of owned, superseded generations only after every reader releases them.</summary>
internal sealed class ImportedGenerationCleanup : IDisposable
{
    private readonly string _imports;
    private readonly ILogger? _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public ImportedGenerationCleanup(AppPaths paths, ILogger? logger = null)
    {
        _logger = logger;
        _imports = Path.GetFullPath(Path.Combine(paths.LocalDataDirectory, "Imports"));
        ImportedGenerationLease.Released += OnReleased;
    }

    private void OnReleased(string generation)
    {
        var installed = Path.GetDirectoryName(generation);
        if (installed is not null && string.Equals(Path.GetDirectoryName(installed), _imports, StringComparison.OrdinalIgnoreCase))
            Request(Path.GetFileName(installed));
    }

    public void Request(string sourceId)
    {
        if (!Guid.TryParseExact(sourceId, "N", out _)) return;
        lock (_sync)
        {
            if (_disposed) return;
            _requests[sourceId] = _requests.GetValueOrDefault(sourceId) + 1;
            if (_workers.ContainsKey(sourceId)) return;
            _workers[sourceId] = Task.Run(() => RunAsync(sourceId));
        }
    }

    private async Task RunAsync(string sourceId)
    {
        var released = false;
        try
        {
            while (true)
            {
                await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);
                long version;
                lock (_sync) version = _requests[sourceId];
                await CleanupAsync(sourceId, _shutdown.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_requests[sourceId] != version) continue;
                    _requests.Remove(sourceId); _workers.Remove(sourceId);
                    released = true;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { _logger?.LogWarning(error, "Could not reclaim older imported generations for {SourceId}.", sourceId); }
        finally { if (!released) lock (_sync) { _requests.Remove(sourceId); _workers.Remove(sourceId); } }
    }

    public Task CleanupAsync(string sourceId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        // FileShare.Delete provides the required exclusion/rename protocol on Windows.
        // Do not infer equivalent cross-process safety from Unix sharing semantics.
        if (!OperatingSystem.IsWindows()) return;
        if (!Guid.TryParseExact(sourceId, "N", out _)) return;
        var installed = Path.Combine(_imports, sourceId);
        try
        {
            if (!Directory.Exists(installed) || IsLink(_imports) || IsLink(installed)) return;
            var sourceFile = Path.Combine(installed, "source.json");
            if (!File.Exists(sourceFile) || IsLink(sourceFile) || new FileInfo(sourceFile).Length > 4 * 1024 * 1024) return;
            var current = JsonSerializer.Deserialize<ImportedWorkspaceSource>(File.ReadAllText(sourceFile), ProfileTransferService.ReadOptions);
            if (current is null || current.Id != sourceId || current.LinkRevision < 1) return;
            var active = Path.Combine(installed, "linked-" + current.LinkRevision);
            if (!Directory.Exists(active) || IsLink(active) || !File.Exists(Path.Combine(active, "metadata.db"))
                || current.HasCatalog && !File.Exists(Path.Combine(active, "catalog.db"))) return;
            foreach (var candidate in Directory.EnumerateDirectories(installed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(candidate);
                var baseName = name;
                if (name.IndexOf(".retired-", StringComparison.Ordinal) is var retiredAt && retiredAt >= 0)
                {
                    if (!Guid.TryParseExact(name[(retiredAt + 9)..], "N", out _)) continue;
                    baseName = name[..retiredAt];
                }
                if (!baseName.StartsWith("linked-", StringComparison.Ordinal) || !int.TryParse(baseName.AsSpan(7), out var revision)
                    || revision < 0 || revision >= current.LinkRevision || baseName != "linked-" + revision) continue;
                TryDelete(candidate);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }, cancellationToken);

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void TryDelete(string directory)
    {
        var handles = new List<FileStream>();
        try
        {
            if (IsLink(directory) || Directory.EnumerateDirectories(directory).Any()) return;
            var files = Directory.GetFiles(directory);
            if (files.Any(file => IsLink(file) || !Allowed(Path.GetFileName(file)))) return;
            var marker = Path.Combine(directory, ImportedGenerationLease.MarkerFileName);
            if (!File.Exists(marker)) return;
            // Shared reader handles deny this open. While held, new readers cannot enter.
            var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            handles.Add(lease);
            if (lease.Length > 128) return;
            using (var reader = new StreamReader(lease, leaveOpen: true))
                if (reader.ReadToEnd() != ImportedGenerationLease.MarkerContent) return;
            // Also guard against an external SQLite reader that does not use our lease protocol.
            foreach (var database in files.Where(file => Path.GetExtension(file) == ".db"))
                handles.Add(new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
            if (Directory.GetFiles(directory).Any(file => IsLink(file) || !Allowed(Path.GetFileName(file)))) return;
            // Retire the pathname atomically before deleting marker/files. A late lazy
            // reader can never mistake a half-deleted marked generation for a legacy one.
            var retired = Path.GetFileName(directory).Contains(".retired-", StringComparison.Ordinal) ? directory : directory + ".retired-" + Guid.NewGuid().ToString("N");
            if (retired != directory) Directory.Move(directory, retired);
            Directory.Delete(retired, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    private static bool Allowed(string name) => name == ImportedGenerationLease.MarkerFileName
        || name is "metadata.db" or "catalog.db" or "metadata.db-wal" or "metadata.db-shm" or "metadata.db-journal"
            or "catalog.db-wal" or "catalog.db-shm" or "catalog.db-journal";

    public void Dispose()
    {
        Task[] workers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ImportedGenerationLease.Released -= OnReleased;
            _shutdown.Cancel(); workers = _workers.Values.ToArray();
        }
        try { Task.WhenAll(workers).GetAwaiter().GetResult(); }
        catch (Exception error) { _logger?.LogDebug(error, "Imported generation cleanup stopped during shutdown."); }
        _shutdown.Dispose();
    }
}
