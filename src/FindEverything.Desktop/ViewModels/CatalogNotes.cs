using System.Collections.Concurrent;

namespace FindEverything.Desktop.ViewModels;

/// <summary>Pending edits outlive virtual rows until persistence succeeds.</summary>
internal static class PendingNoteEditorTracker
{
    public static void Track(ConcurrentDictionary<CatalogUserInputValueViewModel, byte> pending,
        CatalogUserInputValueViewModel editor)
    {
        editor.PropertyChanged += (_, args) =>
        {
            // Value's notification precedes the change-version increment. Do
            // not test HasPendingSave inside this callback or the edit is lost.
            if (args.PropertyName == nameof(CatalogUserInputValueViewModel.Value)
                || args.PropertyName == nameof(CatalogUserInputValueViewModel.HasPendingSave) && editor.HasPendingSave)
                pending.TryAdd(editor, 0);
        };
    }

    public static void Prune(ConcurrentDictionary<CatalogUserInputValueViewModel, byte> pending)
    {
        foreach (var editor in pending.Keys)
            if (!editor.HasPendingSave && pending.TryRemove(editor, out _)
                && editor.HasPendingSave) pending.TryAdd(editor, 0);
    }

    public static IReadOnlyList<CatalogUserInputValueViewModel> Capture(
        ConcurrentDictionary<CatalogUserInputValueViewModel, byte> pending,
        IEnumerable<CatalogUserInputValueViewModel> visible)
    {
        Prune(pending);
        return pending.Keys.Concat(visible).Distinct().Where(static input => input.HasPendingSave).ToArray();
    }
}

internal readonly record struct NoteCellKey(string Profile, string Path, string Field)
{
    public static NoteCellKey Create(string profile, string path, string field) => new(
        profile.ToUpperInvariant(), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)).ToUpperInvariant(),
        field.ToUpperInvariant());
}

/// <summary>Coordinates successive virtual editor instances for the same persisted cell.</summary>
internal sealed class NoteEditSequencer
{
    private readonly ConcurrentDictionary<NoteCellKey, CatalogUserInputValueViewModel> _latest = new();
    private readonly ConcurrentDictionary<NoteCellKey, SemaphoreSlim> _gates = new();

    public void Track(NoteCellKey key, CatalogUserInputValueViewModel editor)
    {
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CatalogUserInputValueViewModel.Value)) _latest[key] = editor;
            else if (args.PropertyName == nameof(CatalogUserInputValueViewModel.HasPendingSave) && editor.HasPendingSave)
                _latest.TryAdd(key, editor);
        };
    }

    public async Task PersistAsync(NoteCellKey key, CatalogUserInputValueViewModel editor, Func<Task> persist)
    {
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Missing means that the latest editor was already saved and
            // pruned. An older delayed editor must still be ignored.
            if (!_latest.TryGetValue(key, out var latest) || !ReferenceEquals(editor, latest)) return;
            await persist().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public void Prune()
    {
        foreach (var pair in _latest)
            if (!pair.Value.HasPendingSave && _latest.TryRemove(pair)
                && pair.Value.HasPendingSave) _latest.TryAdd(pair.Key, pair.Value);
    }
}

public partial class CatalogViewModel
{
    private readonly ConcurrentDictionary<CatalogUserInputValueViewModel, byte> _dirtyNoteEditors = new();
    private readonly NoteEditSequencer _noteEditSequencer = new();

    private void TrackNoteEditors(CatalogItemViewModel row, string? profileId)
    {
        foreach (var editor in row.UserInputValues.Values)
        {
            PendingNoteEditorTracker.Track(_dirtyNoteEditors, editor);
            if (profileId is not null) _noteEditSequencer.Track(NoteCellKey.Create(profileId, row.FullPath, editor.FieldId), editor);
        }
    }

    private Task PersistTrackedNoteAsync(string profileId, string path, string field, string value,
        CatalogUserInputValueViewModel editor) => _noteEditSequencer.PersistAsync(
            NoteCellKey.Create(profileId, path, field), editor, () => PersistUserInputAsync(profileId, path, field, value));

    private void PruneCompletedNoteEditors()
    {
        PendingNoteEditorTracker.Prune(_dirtyNoteEditors);
        _noteEditSequencer.Prune();
    }

    private IReadOnlyList<CatalogUserInputValueViewModel> CapturePendingNoteEditors() =>
        PendingNoteEditorTracker.Capture(_dirtyNoteEditors,
            (_virtualItems?.LoadedItems ?? _loadedItems).SelectMany(static row => row.UserInputValues.Values));

    private async Task CommitTrackedNotesAsync()
    {
        foreach (var editor in CapturePendingNoteEditors()) await editor.CommitAsync(notify: false).ConfigureAwait(true);
        PruneCompletedNoteEditors();
    }
}
