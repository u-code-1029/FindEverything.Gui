using FindEverything.Application.Options;

namespace FindEverything.Desktop.Localization;

/// <summary>
/// Distinguishes an in-app language choice (which already has restart consent)
/// from an external settings edit. The file watcher must not override Later.
/// </summary>
public sealed class LanguageRestartPolicy
{
    private readonly object _gate = new();
    private string? _appOwnedCulture;
    private bool _isWriting;
    private long _revision;

    internal long Revision { get { lock (_gate) return _revision; } }

    internal Change BeginChange(string cultureName)
    {
        lock (_gate)
        {
            var previous = _appOwnedCulture;
            _appOwnedCulture = LocalizationOptions.Normalize(cultureName);
            _isWriting = true;
            return new Change(this, ++_revision, previous);
        }
    }

    internal bool SuppressWatcherRestart(string cultureName, long observedRevision)
    {
        lock (_gate)
        {
            // A reload begun before or during an in-app write cannot invalidate
            // that newer choice using an older settings-file snapshot.
            if (_isWriting || observedRevision != _revision) return true;
            if (IsAppOwned(cultureName)) return true;
            if (_appOwnedCulture is not null)
            {
                // A subsequent, different persisted locale is an external edit.
                // Future external changes must retain their normal behavior.
                _appOwnedCulture = null;
                _revision++;
            }
            return false;
        }
    }

    internal bool AllowsAutomaticRestart(string cultureName)
    {
        lock (_gate) return !_isWriting && !IsAppOwned(cultureName);
    }

    private bool IsAppOwned(string cultureName) =>
        string.Equals(_appOwnedCulture, LocalizationOptions.Normalize(cultureName), StringComparison.Ordinal);

    private void EndChange(long revision, string? previousCulture, bool committed)
    {
        lock (_gate)
        {
            if (_revision != revision) return;
            if (!committed) _appOwnedCulture = previousCulture;
            _isWriting = false;
            _revision++;
        }
    }

    internal sealed class Change(LanguageRestartPolicy owner, long revision, string? previousCulture) : IDisposable
    {
        private bool _ended;

        public void Commit()
        {
            if (_ended) return;
            _ended = true;
            owner.EndChange(revision, previousCulture, committed: true);
        }

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner.EndChange(revision, previousCulture, committed: false);
        }
    }
}
