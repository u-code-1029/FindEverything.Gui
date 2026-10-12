using System.Collections.Concurrent;
using System.IO;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class PendingNoteEditorTrackerTests
{
    [Fact]
    public async Task Older_evicted_editor_cannot_overwrite_a_newer_committed_instance_of_the_same_cell()
    {
        var sequencer = new NoteEditSequencer();
        var key = NoteCellKey.Create("profile", Path.Combine(Path.GetTempPath(), "same-cell"), "note");
        var saved = new List<string>();
        CatalogUserInputValueViewModel? oldEditor = null;
        oldEditor = new("note", null, "", (_, value) => sequencer.PersistAsync(key, oldEditor!,
            () => { saved.Add(value); return Task.CompletedTask; }), () => { });
        CatalogUserInputValueViewModel? newEditor = null;
        newEditor = new("note", null, "", (_, value) => sequencer.PersistAsync(key, newEditor!,
            () => { saved.Add(value); return Task.CompletedTask; }), () => { });
        sequencer.Track(key, oldEditor);
        sequencer.Track(key, newEditor);
        oldEditor.Value = "obsolete edit";
        newEditor.Value = "latest edit";

        await newEditor.CommitAsync(notify: false);
        sequencer.Prune();
        await oldEditor.CommitAsync(notify: false);

        Assert.Equal(["latest edit"], saved);
        Assert.False(oldEditor.HasPendingSave);
        Assert.False(newEditor.HasPendingSave);
    }

    [Fact]
    public async Task Focus_commit_notifies_after_debounce_saved_without_writing_the_value_twice()
    {
        var writes = 0;
        var notifications = 0;
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new CatalogUserInputValueViewModel("note", null, "",
            (_, _) => { Interlocked.Increment(ref writes); persisted.TrySetResult(); return Task.CompletedTask; },
            () => { }, () => Interlocked.Increment(ref notifications));
        editor.Value = "edit";
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Commit waits for the persist gate if debounce is still finishing.
        await editor.CommitAsync();
        await editor.CommitAsync();

        Assert.Equal(1, writes);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Evicted_row_edit_is_still_flushed_before_its_debounce_fires()
    {
        var pending = new ConcurrentDictionary<CatalogUserInputValueViewModel, byte>();
        var saved = string.Empty;
        var editor = new CatalogUserInputValueViewModel("note", null, string.Empty,
            (_, value) => { saved = value; return Task.CompletedTask; }, () => { });
        PendingNoteEditorTracker.Track(pending, editor);
        editor.Value = "latest edit";

        // The virtual list no longer retains this row.
        var flush = PendingNoteEditorTracker.Capture(pending, []);
        Assert.Same(editor, Assert.Single(flush));
        foreach (var input in flush) await input.CommitAsync(notify: false);
        PendingNoteEditorTracker.Prune(pending);

        Assert.Equal("latest edit", saved);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Failed_save_keeps_evicted_edit_available_for_a_retry()
    {
        var pending = new ConcurrentDictionary<CatalogUserInputValueViewModel, byte>();
        var editor = new CatalogUserInputValueViewModel("note", null, string.Empty,
            (_, _) => Task.FromException(new IOException("disk unavailable")), () => { });
        PendingNoteEditorTracker.Track(pending, editor);
        editor.Value = "preserve me";

        await Assert.ThrowsAsync<IOException>(() => editor.CommitAsync(notify: false));
        PendingNoteEditorTracker.Prune(pending);

        Assert.Same(editor, Assert.Single(PendingNoteEditorTracker.Capture(pending, [])));
        Assert.Equal("preserve me", editor.Value);
    }
}
