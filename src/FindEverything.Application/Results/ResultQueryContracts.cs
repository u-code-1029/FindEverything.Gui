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
public sealed class QuerySelection
{
    private readonly HashSet<string> _keys;

    public QuerySelection(bool allMatching = false, IEnumerable<string>? keys = null)
    {
        AllMatching = allMatching;
        _keys = new HashSet<string>(keys ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public bool AllMatching { get; private set; }
    public IReadOnlyCollection<string> Keys => _keys;
    public bool Contains(string key) => AllMatching != _keys.Contains(key);
    public long Count(long totalCount) => AllMatching
        ? Math.Max(0, totalCount - _keys.Count) : _keys.Count;
    public void Set(string key, bool selected)
    {
        if (selected != AllMatching) _keys.Add(key); else _keys.Remove(key);
    }
    public void SelectAll() { AllMatching = true; _keys.Clear(); }
    public void Clear() { AllMatching = false; _keys.Clear(); }
    public void Invert() => AllMatching = !AllMatching;
    public QuerySelection Capture() => new(AllMatching, _keys);
}
