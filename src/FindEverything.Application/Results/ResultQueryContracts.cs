namespace FindEverything.Application.Results;

public sealed record ResultPage<T>(IReadOnlyList<T> Items, int Offset, bool HasMore);

public sealed class ResultQueryChangedException(string expectedIdentity, string? actualIdentity)
    : InvalidOperationException("The result generation changed. Start a new query before reading more rows.")
{
    public string ExpectedIdentity { get; } = expectedIdentity;
    public string? ActualIdentity { get; } = actualIdentity;
}

/// <summary>A query has fixed filters and generation; pages never mix publications.</summary>
public interface IResultQuerySession<T> : IAsyncDisposable
{
    string Identity { get; }
    Task<long> CountAsync(CancellationToken cancellationToken = default);
    Task<ResultPage<T>> ReadRangeAsync(int offset, int limit,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> EnumerateSelectionAsync(QuerySelection selection,
        CancellationToken cancellationToken = default);
}

/// <summary>All matching rows minus exceptions, or explicit keys; never all row objects.</summary>
public interface IQuerySelectionKeySet : IDisposable
{
    long Count { get; }
    /// <summary>Private immutable SQLite database containing selection_baseline(stable_key,query_key).</summary>
    string DatabasePath { get; }
    bool Contains(string stableKey);
    bool ContainsQueryKey(string key) => Contains(key);
    IQuerySelectionKeySet Retain();
}

public sealed class QuerySelection : IDisposable
{
    private readonly HashSet<string> _keys;
    private IQuerySelectionKeySet? _baseline;
    private long _baselineExceptionCount;
    private long _baselineUnavailableCount;
    private readonly HashSet<string> _unavailable = new(StringComparer.OrdinalIgnoreCase);

    public QuerySelection(bool allMatching = false, IEnumerable<string>? keys = null)
    {
        AllMatching = allMatching;
        _keys = new HashSet<string>(keys ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public bool AllMatching { get; private set; }
    public IReadOnlyCollection<string> Keys => _keys;
    public IReadOnlyCollection<string> UnavailableKeys => _unavailable;
    public IQuerySelectionKeySet? Baseline => _baseline;
    public bool Contains(string key) => !_unavailable.Contains(key)
        && (AllMatching ^ (_baseline?.Contains(key) ?? false) ^ _keys.Contains(key));
    public long Count(long totalCount)
    {
        var selected = (_baseline?.Count ?? 0) - _baselineUnavailableCount + _keys.Count - 2 * _baselineExceptionCount;
        return Math.Clamp(AllMatching ? totalCount - selected : selected, 0, totalCount);
    }
    public void Set(string key, bool selected)
    {
        var baselineContains = _baseline?.Contains(key) ?? false;
        if (_unavailable.Remove(key) && baselineContains) _baselineUnavailableCount--;
        if (selected != (AllMatching ^ baselineContains))
        { if (_keys.Add(key) && baselineContains) _baselineExceptionCount++; }
        else if (_keys.Remove(key) && baselineContains) _baselineExceptionCount--;
    }
    public void SelectAll() { Clear(); AllMatching = true; }
    public void Clear() { _baseline?.Dispose(); _baseline = null; AllMatching = false; _keys.Clear(); _unavailable.Clear(); _baselineExceptionCount = 0; _baselineUnavailableCount = 0; }
    public void Invert() => AllMatching = !AllMatching;
    public void ReplaceBaseline(IQuerySelectionKeySet keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var retained = keys.Retain();
        Clear();
        _baseline = retained;
    }
    /// <summary>A single edited row left the fixed predicate universe. Preserve every other selection.</summary>
    public void RemoveUnavailableKey(string key)
    {
        if (!_unavailable.Add(key)) return;
        var member = _baseline?.Contains(key) ?? false;
        if (member) _baselineUnavailableCount++;
        if (_keys.Remove(key) && member) _baselineExceptionCount--;
    }
    public QuerySelection Capture()
    {
        var captured = new QuerySelection(AllMatching, _keys)
        { _baseline = _baseline?.Retain(), _baselineExceptionCount = _baselineExceptionCount,
          _baselineUnavailableCount = _baselineUnavailableCount };
        captured._unavailable.UnionWith(_unavailable);
        return captured;
    }
    /// <summary>Only explicit overrides are translated. Baseline query keys already belong to the original query.</summary>
    public QuerySelection MapKeys(Func<string, string> mapper)
    {
        var mapped = new QuerySelection(AllMatching, _keys.Select(mapper))
        { _baseline = _baseline is null ? null : new MappedKeySet(_baseline.Retain()), _baselineExceptionCount = _baselineExceptionCount,
          _baselineUnavailableCount = _baselineUnavailableCount };
        mapped._unavailable.UnionWith(_unavailable.Select(mapper));
        return mapped;
    }
    public void Dispose() => Clear();

    private sealed class MappedKeySet(IQuerySelectionKeySet inner) : IQuerySelectionKeySet
    {
        public long Count => inner.Count;
        public string DatabasePath => inner.DatabasePath;
        public bool Contains(string key) => inner.ContainsQueryKey(key);
        public bool ContainsQueryKey(string key) => inner.ContainsQueryKey(key);
        public IQuerySelectionKeySet Retain() => new MappedKeySet(inner.Retain());
        public void Dispose() => inner.Dispose();
    }
}
