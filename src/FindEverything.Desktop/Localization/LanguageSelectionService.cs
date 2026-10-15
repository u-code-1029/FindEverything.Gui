using FindEverything.Application.Options;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Localization;

public enum LanguageRestartDecision { Cancel, Later, RestartNow }

public interface ILanguageRestartPrompt
{
    Task<LanguageRestartDecision> ChooseAsync(string cultureName, bool operationRunning, CancellationToken cancellationToken);
}

public sealed class ContentDialogLanguageRestartPrompt(IContentDialogService dialogs, IAppLocalizer localizer,
    IUiScheduler scheduler) : ILanguageRestartPrompt
{
    public Task<LanguageRestartDecision> ChooseAsync(string cultureName, bool operationRunning, CancellationToken cancellationToken) =>
        scheduler.InvokeAsync(async () =>
        {
            var result = await dialogs.ShowAsync(new ContentDialog
            {
                Title = localizer.Get("Loc.Ux.Shell.Language.Title", "언어를 적용하려면 다시 시작해야 합니다"),
                Content = localizer.Get(operationRunning ? "Loc.Ux.Shell.Language.Busy" : "Loc.Ux.Shell.Language.Message",
                    operationRunning ? "작업이 진행 중입니다. 언어를 저장한 뒤, 작업이 끝나면 직접 다시 시작하세요." : "지금 다시 시작하거나 언어만 저장하고 나중에 적용할 수 있습니다."),
                PrimaryButtonText = localizer.Get("Loc.Ux.Shell.Language.Now", "지금 다시 시작"),
                IsPrimaryButtonEnabled = !operationRunning,
                SecondaryButtonText = localizer.Get("Loc.Ux.Shell.Language.Later", "저장하고 나중에 적용"),
                CloseButtonText = localizer.Get("Loc.Common.Cancel", "취소"),
                DefaultButton = ContentDialogButton.Close,
            }, cancellationToken);
            return result switch { ContentDialogResult.Primary => LanguageRestartDecision.RestartNow,
                ContentDialogResult.Secondary => LanguageRestartDecision.Later, _ => LanguageRestartDecision.Cancel };
        });
}

public interface ILanguageSelectionService
{
    string CurrentCultureName { get; }
    string? PendingCultureName { get; }
    event EventHandler? PendingChanged;
    Task<bool> ChangeAsync(string cultureName, CancellationToken cancellationToken = default);
}

public sealed class LanguageSelectionService(IAppLocalizer localizer, IUserSettingsWriter settingsWriter,
    IApplicationRestartCoordinator restartCoordinator, IDraftProtectionService? draftProtection = null,
    ILanguageRestartPrompt? prompt = null, IApplicationOperationCoordinator? operations = null,
    IUiScheduler? scheduler = null, CatalogActivityState? activity = null,
    LanguageRestartPolicy? languageRestartPolicy = null) : ILanguageSelectionService
{
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    public string CurrentCultureName => LocalizationOptions.Normalize(localizer.Culture.Name);
    public string? PendingCultureName { get; private set; }
    public event EventHandler? PendingChanged;
    private bool IsWorking => operations?.IsRunning == true || activity?.IsScanning == true || activity?.IsFilesScanning == true;

    public async Task<bool> ChangeAsync(string cultureName, CancellationToken cancellationToken = default)
    {
        var requested = LocalizationOptions.Normalize(cultureName);
        if (requested == CurrentCultureName && PendingCultureName is null) return false;
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requested == CurrentCultureName && PendingCultureName is null) return false;
            // Returning to the running language cancels a deferred preference without a restart.
            if (requested == CurrentCultureName)
            {
                using var change = languageRestartPolicy?.BeginChange(requested);
                await settingsWriter.SaveAsync(new UserSettingsUpdate(Localization: new() { CultureName = requested }), cancellationToken).ConfigureAwait(false);
                change?.Commit();
                SetPending(null);
                return true;
            }
            var choice = prompt is null ? LanguageRestartDecision.Later
                : await prompt.ChooseAsync(requested, IsWorking, cancellationToken).ConfigureAwait(false);
            if (choice == LanguageRestartDecision.Cancel) return false;
            if (choice == LanguageRestartDecision.RestartNow && IsWorking) return false;
            if (choice == LanguageRestartDecision.RestartNow && draftProtection is not null
                && !await draftProtection.ConfirmAllAsync().ConfigureAwait(false)) return false;
            // Work can start while the confirmation/save dialog is open. Never interrupt it.
            if (choice == LanguageRestartDecision.RestartNow && IsWorking) return false;
            // Register before writing: FileSystemWatcher can read the new file
            // before SaveAsync returns. A failed write restores the prior policy.
            using var preference = languageRestartPolicy?.BeginChange(requested);
            await settingsWriter.SaveAsync(new UserSettingsUpdate(Localization: new() { CultureName = requested }), cancellationToken).ConfigureAwait(false);
            preference?.Commit();
            await (scheduler ?? ImmediateUiScheduler.Instance).InvokeAsync(() =>
            {
                if (choice == LanguageRestartDecision.Later || IsWorking) SetPending(requested);
                else restartCoordinator.RequestRestart();
                return Task.FromResult(true);
            }).ConfigureAwait(false);
            return true;
        }
        finally { _changeGate.Release(); }
    }

    private void SetPending(string? value)
    {
        PendingCultureName = value;
        (scheduler ?? ImmediateUiScheduler.Instance).Post(() => PendingChanged?.Invoke(this, EventArgs.Empty));
    }
}
