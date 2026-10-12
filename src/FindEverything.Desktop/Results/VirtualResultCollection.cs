using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Channels;
using FindEverything.Application.Results;

namespace FindEverything.Desktop.Results;

/// <summary>A virtual IList with a bounded page cache. Index reads never block the UI.</summary>
public sealed class VirtualResultCollection<T> : IList<T>, IList,
    INotifyCollectionChanged, INotifyPropertyChanged, IDisposable where T : class
{
    public const int PageSize = 500;
    private const int MaximumPages = 10;
    private readonly Func<int, int, CancellationToken, Task<ResultPage<T>>> _load;
    private readonly Func<int, T> _placeholder;
    private readonly Action<Action> _dispatch;
    private readonly Action<Exception> _failed;
    private readonly Dictionary<int, T[]> _pages = [];
    private readonly HashSet<int> _loading = [];
    private readonly HashSet<int> _ready = [];
    private readonly LinkedList<int> _recent = [];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Channel<int> _pending = Channel.CreateBounded<int>(new BoundedChannelOptions(MaximumPages)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = false,
        SingleReader = false,
    });
    private readonly object _gate = new();
    private readonly Task[] _workers;
    private int _count;
    private volatile bool _disposed;
    private int _hasFailure;

    public VirtualResultCollection(Func<int, int, CancellationToken, Task<ResultPage<T>>> load,
        Func<int, T> placeholder, Action<Action> dispatch, Action<Exception> failed)
    {
        _load = load; _placeholder = placeholder; _dispatch = dispatch; _failed = failed;
        // SQLite can execute synchronously before its first await. Page reads
        // always begin on these workers, never on the UI's indexer call stack.
        _workers = [Task.Run(LoadWorkerAsync), Task.Run(LoadWorkerAsync)];
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PageLoaded;
    public int Count => _count;
    public bool IsReadOnly => true;
    public int CachedItemCount { get { lock (_gate) return _pages.Where(p => _ready.Contains(p.Key)).Sum(p => p.Value.Length); } }
    public IReadOnlyList<T> LoadedItems
    {
        get { lock (_gate) return _pages.OrderBy(p => p.Key).Where(p => _ready.Contains(p.Key)).SelectMany(p => p.Value).ToArray(); }
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            var start = index / PageSize * PageSize;
            T value;
            lock (_gate)
            {
                if (!_pages.TryGetValue(start, out var page))
                {
                    page = Enumerable.Range(start, Math.Min(PageSize, Count - start)).Select(_placeholder).ToArray();
                    _pages[start] = page;
                }
                Touch(start);
                if (!_disposed && Volatile.Read(ref _hasFailure) == 0
                    && !_ready.Contains(start) && !_loading.Contains(start))
                {
                    _loading.Add(start);
                    if (!_pending.Writer.TryWrite(start)) _loading.Remove(start);
                }
                value = page[index - start];
            }
            return value;
        }
        set => throw new NotSupportedException();
    }

    public void SetCount(long count)
    {
        var value = checked((int)count);
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (value == _count) return;
        _count = value;
        lock (_gate)
        {
            foreach (var start in _pages.Keys.ToArray())
            {
                if (start >= value) { _pages.Remove(start); _ready.Remove(start); _recent.Remove(start); }
                else if (_pages[start].Length > value - start) _pages[start] = _pages[start][..(value - start)];
            }
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Seed(ResultPage<T> page, long? count = null)
    {
        lock (_gate) { _pages[page.Offset] = page.Items.ToArray(); _ready.Add(page.Offset); _loading.Remove(page.Offset); Touch(page.Offset); }
        SetCount(count ?? page.Offset + page.Items.Count);
        PageLoaded?.Invoke(this, EventArgs.Empty);
    }

    public int IndexOf(T item)
    {
        lock (_gate)
        {
            foreach (var (start, page) in _pages)
            {
                var local = Array.IndexOf(page, item);
                if (local >= 0) return start + local;
            }
        }
        return -1;
    }

    private void Touch(int start)
    {
        _recent.Remove(start); _recent.AddLast(start);
        while (_recent.Count > MaximumPages)
        {
            var first = _recent.First!.Value;
            _recent.RemoveFirst();
            _pages.Remove(first);
            _ready.Remove(first);
        }
    }

    private void QueueVisiblePages()
    {
        if (_disposed || Volatile.Read(ref _hasFailure) != 0) return;
        foreach (var start in _recent.Reverse())
        {
            if (_ready.Contains(start) || _loading.Contains(start)) continue;
            _loading.Add(start);
            if (!_pending.Writer.TryWrite(start)) { _loading.Remove(start); break; }
        }
    }

    private async Task LoadWorkerAsync()
    {
        try
        {
            await foreach (var start in _pending.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (!_pages.ContainsKey(start))
                    {
                        _loading.Remove(start);
                        QueueVisiblePages();
                        continue;
                    }
                }
                try
                {
                    var result = await _load(start, PageSize, _cancellation.Token).ConfigureAwait(false);
                    _dispatch(() => Publish(start, result));
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    lock (_gate) _loading.Remove(start);
                    if (Interlocked.Exchange(ref _hasFailure, 1) == 0)
                        _dispatch(() => { if (!_disposed) _failed(exception); });
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    private void Publish(int start, ResultPage<T> result)
    {
        if (_disposed) return;
        T[]? previous;
        T[] next;
        lock (_gate)
        {
            _loading.Remove(start);
            if (!_pages.TryGetValue(start, out previous) || start >= Count) return;
            next = result.Items.Take(Count - start).ToArray();
            _pages[start] = next; _ready.Add(start); Touch(start);
            QueueVisiblePages();
        }
        for (var index = 0; index < next.Length && index < previous.Length; index++)
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace, next[index], previous[index], start + index));
        PageLoaded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _cancellation.Cancel(); _pending.Writer.TryComplete();
        _ = Task.WhenAll(_workers).ContinueWith(task =>
        {
            _ = task.Exception;
            _cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public bool Contains(T item) => IndexOf(item) >= 0;
    public IEnumerator<T> GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(T[] array, int index) { foreach (var item in this) array[index++] = item; }
    public void Add(T item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, T item) => throw new NotSupportedException();
    public bool Remove(T item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    bool IList.IsFixedSize => true;
    object ICollection.SyncRoot => _gate;
    bool ICollection.IsSynchronized => false;
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    int IList.Add(object? item) => throw new NotSupportedException();
    bool IList.Contains(object? item) => item is T value && Contains(value);
    int IList.IndexOf(object? item) => item is T value ? IndexOf(value) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) { foreach (var item in this) array.SetValue(item, index++); }
}
