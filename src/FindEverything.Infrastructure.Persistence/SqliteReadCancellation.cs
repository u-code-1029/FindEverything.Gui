using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FindEverything.Infrastructure.Persistence;

/// <summary>Microsoft.Data.Sqlite async methods are synchronous and Cancel is a no-op.
/// Interrupt only this operation's private, unpooled read connection.</summary>
internal sealed class SqliteReadCancellation : IDisposable
{
    private readonly sqlite3 _handle;
    private readonly CancellationTokenRegistration _registration;
    private readonly delegate_progress _progress;
    private bool _addedReference;

    public SqliteReadCancellation(SqliteConnection connection, CancellationToken cancellationToken)
    {
        _handle = connection.Handle!;
        _handle.DangerousAddRef(ref _addedReference);
        try
        {
            _progress = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
            raw.sqlite3_progress_handler(_handle, 1000, _progress, null!);
            _registration = cancellationToken.Register(static state => raw.sqlite3_interrupt((sqlite3)state!), _handle);
        }
        catch
        {
            if (_addedReference) _handle.DangerousRelease();
            throw;
        }
    }

    public void Dispose()
    {
        // Dispose waits for an executing callback before releasing the handle.
        _registration.Dispose();
        raw.sqlite3_progress_handler(_handle, 0, null!, null!);
        if (_addedReference) { _handle.DangerousRelease(); _addedReference = false; }
        GC.KeepAlive(_progress);
    }

    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await operation(token).ConfigureAwait(false); }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 && started.Elapsed < TimeSpan.FromSeconds(30))
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }
}
