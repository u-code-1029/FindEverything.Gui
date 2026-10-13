using FindEverything.Application.Results;
using Microsoft.Data.Sqlite;

namespace FindEverything.Infrastructure.Persistence;

/// <summary>Reference-counted immutable private key database. UI membership lookups have a bounded cache.</summary>
internal sealed class SqliteSelectionKeySet : IQuerySelectionKeySet
{
    private sealed class Shared(string path, long count)
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, bool> Cache = new(StringComparer.Ordinal);
        public readonly SqliteConnection Connection = Open(path);
        public readonly string Path = path;
        public readonly long Count = count;
        public int References = 1;
        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
    }
    private Shared? _shared;
    public SqliteSelectionKeySet(string path, long count) => _shared = new(path, count);
    private SqliteSelectionKeySet(Shared shared) => _shared = shared;
    public long Count => SharedValue.Count;
    public string DatabasePath => SharedValue.Path;
    private Shared SharedValue => _shared ?? throw new ObjectDisposedException(nameof(SqliteSelectionKeySet));
    public bool Contains(string stableKey)
        => ContainsCore(stableKey, "stable_key", "s:");
    public bool ContainsQueryKey(string key) => ContainsCore(key, "query_key", "q:");
    private bool ContainsCore(string stableKey, string column, string prefix)
    {
        var shared = SharedValue;
        var key = stableKey.ToUpperInvariant();
        lock (shared.Gate)
        {
            if (shared.Cache.TryGetValue(prefix + key, out var cached)) return cached;
            using var command = shared.Connection.CreateCommand();
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM selection_baseline WHERE {column}=$key);";
            command.Parameters.AddWithValue("$key", key);
            var result = Convert.ToInt64(command.ExecuteScalar()) != 0;
            if (shared.Cache.Count >= 8192) shared.Cache.Clear();
            shared.Cache[prefix + key] = result;
            return result;
        }
    }
    public IQuerySelectionKeySet Retain()
    {
        var shared = SharedValue;
        lock (shared.Gate) { checked { shared.References++; } }
        return new SqliteSelectionKeySet(shared);
    }
    public void Dispose()
    {
        var shared = Interlocked.Exchange(ref _shared, null);
        if (shared is null) return;
        lock (shared.Gate)
        {
            if (--shared.References != 0) return;
            shared.Connection.Dispose();
            try { File.Delete(shared.Path); } catch (IOException) { }
            try { Directory.Delete(System.IO.Path.GetDirectoryName(shared.Path)!); } catch (IOException) { }
        }
        GC.SuppressFinalize(this);
    }
    ~SqliteSelectionKeySet() { try { Dispose(); } catch { /* Process-exit cleanup is best effort. */ } }
}
