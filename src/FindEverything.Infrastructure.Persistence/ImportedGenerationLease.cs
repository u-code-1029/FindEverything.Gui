namespace FindEverything.Infrastructure.Persistence;

/// <summary>Protects lazy imported queries, including across application processes.</summary>
public static class ImportedGenerationLease
{
    public const string MarkerFileName = ".reader-lease";
    public const string MarkerContent = "FindEverything.GenerationLease.v1";
    public static event Action<string>? Released;

    public static IDisposable Acquire(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var parent = Path.GetDirectoryName(directory);
        if (!Path.GetFileName(directory).StartsWith("linked-", StringComparison.Ordinal)
            || parent is null || !Guid.TryParseExact(Path.GetFileName(parent), "N", out _)) return Empty.Instance;
        var marker = Path.Combine(directory, MarkerFileName);
        // Legacy generations are deliberately not eligible for automatic reclamation.
        if (!File.Exists(marker))
        {
            if (!Directory.Exists(directory) && File.Exists(Path.Combine(parent, "source.json")))
                throw new IOException("This shared generation is no longer available. Reload the current shared source.");
            return Empty.Instance;
        }
        return new Reader(directory, new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    private sealed class Reader(string directory, FileStream handle) : IDisposable
    {
        private FileStream? _handle = handle;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _handle, null);
            if (owned is null) return;
            owned.Dispose();
            if (Released is not { } listeners) return;
            foreach (Action<string> listener in listeners.GetInvocationList())
                try { listener(directory); } catch (Exception) { /* Reclamation never breaks query disposal. */ }
        }
    }

    private sealed class Empty : IDisposable
    {
        internal static readonly Empty Instance = new();
        public void Dispose() { }
    }
}
