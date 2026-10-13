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
    Task<IReadOnlySet<string>> ReadMembershipAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlySet<string>>(() =>
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Contains(key)) matches.Add(key);
        }
        return matches;
    }, cancellationToken);
    Task<IReadOnlySet<string>> ReadQueryMembershipAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlySet<string>>(() =>
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsQueryKey(key)) matches.Add(key);
        }
        return matches;
    }, cancellationToken);
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
    internal bool ContainsResolvedMembership(string key, bool baselineContains) => !_unavailable.Contains(key)
        && (AllMatching ^ baselineContains ^ _keys.Contains(key));
    public long Count(long totalCount)
    {
        var selected = (_baseline?.Count ?? 0) - _baselineUnavailableCount + _keys.Count - 2 * _baselineExceptionCount;
        return Math.Clamp(AllMatching ? totalCount - selected : selected, 0, totalCount);
    }
    public void Set(string key, bool selected)
    {
        var baselineContains = _baseline?.Contains(key) ?? false;
        SetResolvedMembership(key, selected, baselineContains);
    }
    internal void SetResolvedMembership(string key, bool selected, bool baselineContains)
    {
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
        var member = _baseline?.Contains(key) ?? false;
        RemoveUnavailableKey(key, member);
    }
    private void RemoveUnavailableKey(string key, bool member)
    {
        if (!_unavailable.Add(key)) return;
        if (member) _baselineUnavailableCount++;
        if (_keys.Remove(key) && member) _baselineExceptionCount--;
    }
    /// <summary>Resolves membership off-thread, then applies to the caller-owned state.</summary>
    public async Task RemoveUnavailableKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = _baseline;
            if (baseline is null) { RemoveUnavailableKey(key, false); return; }
            using var lease = baseline.Retain();
            var matches = await lease.ReadMembershipAsync([key], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Selection may have been cleared/replaced while the storage read ran.
            if (!ReferenceEquals(baseline, _baseline)) continue;
            RemoveUnavailableKey(key, matches.Contains(key));
            return;
        }
    }
    public QuerySelection Capture()
    {
        var captured = new QuerySelection(AllMatching, _keys)
        { _baseline = _baseline?.Retain(), _baselineExceptionCount = _baselineExceptionCount,
          _baselineUnavailableCount = _baselineUnavailableCount };
        captured._unavailable.UnionWith(_unavailable);
        return captured;
    }
    /// <summary>Adopts a prepared selection without storage lookups for each key.</summary>
    public void ReplaceWith(QuerySelection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source)) return;
        var baseline = source._baseline?.Retain();
        Clear();
        _baseline = baseline;
        AllMatching = source.AllMatching;
        _keys.UnionWith(source._keys);
        _unavailable.UnionWith(source._unavailable);
        _baselineExceptionCount = source._baselineExceptionCount;
        _baselineUnavailableCount = source._baselineUnavailableCount;
    }
    /// <summary>Reads a stable snapshot's flags in a bounded storage batch.</summary>
    public async Task<IReadOnlyList<bool>> ReadMembershipAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        var baseline = _baseline is null ? null
            : await _baseline.ReadMembershipAsync(keys, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return keys.Select(key => !_unavailable.Contains(key)
            && (AllMatching ^ (baseline?.Contains(key) ?? false) ^ _keys.Contains(key))).ToArray();
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
        public Task<IReadOnlySet<string>> ReadMembershipAsync(IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default) => inner.ReadQueryMembershipAsync(keys, cancellationToken);
        public Task<IReadOnlySet<string>> ReadQueryMembershipAsync(IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default) => inner.ReadQueryMembershipAsync(keys, cancellationToken);
        public IQuerySelectionKeySet Retain() => new MappedKeySet(inner.Retain());
        public void Dispose() => inner.Dispose();
    }
}
