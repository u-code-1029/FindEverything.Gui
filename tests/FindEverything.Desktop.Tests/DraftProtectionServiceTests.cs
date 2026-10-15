using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class DraftProtectionServiceTests
{
    [Fact]
    public async Task Cancel_blocks_replacement_and_does_not_save_or_discard()
    {
        var draft = new RecordingDraft();
        var service = Create(DraftLeaveDecision.Cancel);

        Assert.False(await service.ConfirmAsync(draft));
        Assert.True(draft.HasUnsavedChanges);
        Assert.Equal(0, draft.SaveCount);
        Assert.Equal(0, draft.DiscardCount);
    }

    [Fact]
    public async Task Failed_save_blocks_replacement_and_keeps_draft()
    {
        var draft = new RecordingDraft { SaveSucceeds = false };

        Assert.False(await Create(DraftLeaveDecision.Save).ConfirmAsync(draft));
        Assert.True(draft.HasUnsavedChanges);
        Assert.Equal(1, draft.SaveCount);
        Assert.Equal(0, draft.DiscardCount);
    }

    [Fact]
    public async Task Successful_save_allows_replacement_only_after_changes_are_saved()
    {
        var draft = new RecordingDraft();

        Assert.True(await Create(DraftLeaveDecision.Save).ConfirmAsync(draft));
        Assert.False(draft.HasUnsavedChanges);
        Assert.Equal(1, draft.SaveCount);
    }

    [Fact]
    public async Task Later_cancel_does_not_discard_an_earlier_draft_during_app_exit()
    {
        var first = new RecordingDraft();
        var second = new RecordingDraft();
        var service = Create(DraftLeaveDecision.Discard, DraftLeaveDecision.Cancel);
        service.Register(first);
        service.Register(second);

        Assert.False(await service.ConfirmAllAsync());
        Assert.True(first.HasUnsavedChanges);
        Assert.True(second.HasUnsavedChanges);
        Assert.Equal(0, first.DiscardCount);
    }

    [Fact]
    public async Task Untouched_drafts_never_show_a_prompt()
    {
        var prompt = new RecordingPrompt([]);
        var service = new DraftProtectionService(prompt, ImmediateUiScheduler.Instance);

        Assert.True(await service.ConfirmAsync(new RecordingDraft { HasUnsavedChanges = false }));
        Assert.Equal(0, prompt.Count);
    }

    [Fact]
    public async Task Busy_participants_block_exit_even_when_their_draft_is_clean()
    {
        var prompt = new RecordingPrompt([]);
        var service = new DraftProtectionService(prompt, ImmediateUiScheduler.Instance);
        var draft = new RecordingDraft { HasUnsavedChanges = false, CanLeaveDraft = false };

        Assert.False(await service.ConfirmAsync(draft));
        Assert.Equal(0, prompt.Count);
        Assert.Equal(0, draft.DiscardCount);
        Assert.Equal(0, draft.SaveCount);
    }

    [Theory]
    [InlineData(DraftLeaveDecision.Discard)]
    [InlineData(DraftLeaveDecision.Save)]
    public async Task A_save_started_while_the_prompt_is_open_prevents_the_transition(DraftLeaveDecision decision)
    {
        var prompt = new DeferredPrompt();
        var draft = new RecordingDraft();
        var service = new DraftProtectionService(prompt, ImmediateUiScheduler.Instance);
        var transition = service.ConfirmAsync(draft);
        Assert.False(transition.IsCompleted);
        draft.CanLeaveDraft = false;
        prompt.Complete(decision);

        Assert.False(await transition);
        Assert.True(draft.HasUnsavedChanges);
        Assert.Equal(0, draft.SaveCount);
        Assert.Equal(0, draft.DiscardCount);
    }

    [Fact]
    public async Task A_busy_participant_after_another_draft_saves_keeps_deferred_discards()
    {
        var first = new RecordingDraft();
        var second = new RecordingDraft { OnSave = () => first.CanLeaveDraft = false };
        var service = Create(DraftLeaveDecision.Discard, DraftLeaveDecision.Save);
        service.Register(first);
        service.Register(second);

        Assert.False(await service.ConfirmAllAsync());
        Assert.Equal(0, first.DiscardCount);
        Assert.True(first.HasUnsavedChanges);
    }

    private static DraftProtectionService Create(params DraftLeaveDecision[] decisions) =>
        new(new RecordingPrompt(decisions), ImmediateUiScheduler.Instance);

    private sealed class RecordingPrompt(DraftLeaveDecision[] decisions) : IDraftDecisionPrompt
    {
        public int Count { get; private set; }
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) =>
            Task.FromResult(decisions[Count++]);
    }

    private sealed class DeferredPrompt : IDraftDecisionPrompt
    {
        private readonly TaskCompletionSource<DraftLeaveDecision> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) => _completion.Task;
        public void Complete(DraftLeaveDecision decision) => _completion.SetResult(decision);
    }

    private sealed class RecordingDraft : IDraftParticipant
    {
        public string DraftTitle => "Draft";
        public bool HasUnsavedChanges { get; set; } = true;
        public bool CanLeaveDraft { get; set; } = true;
        public bool SaveSucceeds { get; init; } = true;
        public Action? OnSave { get; init; }
        public int SaveCount { get; private set; }
        public int DiscardCount { get; private set; }
        public Task<bool> SaveChangesAsync()
        {
            SaveCount++;
            if (SaveSucceeds) HasUnsavedChanges = false;
            OnSave?.Invoke();
            return Task.FromResult(SaveSucceeds);
        }
        public void DiscardChanges()
        {
            DiscardCount++;
            HasUnsavedChanges = false;
        }
    }
}
