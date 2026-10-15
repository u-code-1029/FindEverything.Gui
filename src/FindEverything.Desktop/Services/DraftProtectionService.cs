using FindEverything.Desktop.Localization;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Services;

public enum DraftLeaveDecision { Save, Discard, Cancel }

public interface IDraftParticipant
{
    string DraftTitle { get; }
    bool HasUnsavedChanges { get; }
    bool CanLeaveDraft => true;
    Task<bool> SaveChangesAsync();
    void DiscardChanges();
}

public interface IDraftDecisionPrompt
{
    Task<DraftLeaveDecision> ChooseAsync(string draftTitle);
}

public sealed class ContentDialogDraftDecisionPrompt(
    IContentDialogService dialogs,
    IAppLocalizer localizer) : IDraftDecisionPrompt
{
    public async Task<DraftLeaveDecision> ChooseAsync(string draftTitle)
    {
        var result = await dialogs.ShowAsync(new ContentDialog
        {
            Title = localizer.Get("Loc.Draft.Leave.Title", "변경 사항을 저장할까요?"),
            Content = localizer.Format("Loc.Draft.Leave.Message", "'{0}'에 저장하지 않은 변경 사항이 있습니다.", draftTitle),
            PrimaryButtonText = localizer.Get("Loc.Common.Save", "저장"),
            SecondaryButtonText = localizer.Get("Loc.Draft.Leave.Discard", "저장하지 않고 이동"),
            CloseButtonText = localizer.Get("Loc.Common.Cancel", "취소"),
            DefaultButton = ContentDialogButton.Close,
        }, CancellationToken.None).ConfigureAwait(true);

        return result switch
        {
            ContentDialogResult.Primary => DraftLeaveDecision.Save,
            ContentDialogResult.Secondary => DraftLeaveDecision.Discard,
            _ => DraftLeaveDecision.Cancel,
        };
    }
}

public interface IDraftProtectionService
{
    void Register(IDraftParticipant participant);
    Task<bool> ConfirmAsync(IDraftParticipant participant);
    Task<bool> ConfirmAllAsync();
}

/// <summary>One policy for draft replacement, app exit, and language restarts.</summary>
public sealed class DraftProtectionService(
    IDraftDecisionPrompt prompt,
    IUiScheduler scheduler) : IDraftProtectionService
{
    private readonly List<IDraftParticipant> _participants = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Register(IDraftParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_participants.Contains(participant)) _participants.Add(participant);
    }

    public Task<bool> ConfirmAsync(IDraftParticipant participant) =>
        scheduler.InvokeAsync(() => ConfirmCoreAsync([participant]));

    public Task<bool> ConfirmAllAsync() =>
        scheduler.InvokeAsync(() => ConfirmCoreAsync(_participants.ToArray()));

    private async Task<bool> ConfirmCoreAsync(IReadOnlyList<IDraftParticipant> participants)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (participants.Any(participant => !participant.CanLeaveDraft)) return false;
            var discarded = new List<IDraftParticipant>();
            foreach (var participant in participants)
            {
                if (!participant.HasUnsavedChanges) continue;
                if (!participant.CanLeaveDraft) return false;
                var decision = await prompt.ChooseAsync(participant.DraftTitle).ConfigureAwait(true);
                if (participants.Any(candidate => !candidate.CanLeaveDraft)) return false;
                if (decision == DraftLeaveDecision.Cancel) return false;
                if (decision == DraftLeaveDecision.Save)
                {
                    if (!await participant.SaveChangesAsync().ConfigureAwait(true)
                        || participant.HasUnsavedChanges
                        || participants.Any(candidate => !candidate.CanLeaveDraft)) return false;
                }
                else
                {
                    // A later draft may cancel the shared transition. Only
                    // commit discard decisions after every draft agrees.
                    discarded.Add(participant);
                }
            }

            if (participants.Any(participant => !participant.CanLeaveDraft)) return false;
            foreach (var participant in discarded) participant.DiscardChanges();
            return true;
        }
        finally { _gate.Release(); }
    }
}
