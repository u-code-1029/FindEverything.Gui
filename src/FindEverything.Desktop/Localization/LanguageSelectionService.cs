using FindEverything.Application.Options;
using FindEverything.Desktop.Hosting;

namespace FindEverything.Desktop.Localization;

public interface ILanguageSelectionService
{
    string CurrentCultureName { get; }

    Task<bool> ChangeAsync(
        string cultureName,
        CancellationToken cancellationToken = default);
}

public sealed class LanguageSelectionService(
    IAppLocalizer localizer,
    IUserSettingsWriter settingsWriter,
    IApplicationRestartCoordinator restartCoordinator) : ILanguageSelectionService
{
    private readonly SemaphoreSlim _changeGate = new(1, 1);

    public string CurrentCultureName => LocalizationOptions.Normalize(
        localizer.Culture.Name);

    public async Task<bool> ChangeAsync(
        string cultureName,
        CancellationToken cancellationToken = default)
    {
        var normalizedCultureName = LocalizationOptions.Normalize(cultureName);
        if (string.Equals(
                normalizedCultureName,
                CurrentCultureName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(
                    normalizedCultureName,
                    CurrentCultureName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await settingsWriter.SaveAsync(
                new UserSettingsUpdate(
                    Localization: new LocalizationOptions
                    {
                        CultureName = normalizedCultureName,
                    }),
                cancellationToken).ConfigureAwait(false);

            restartCoordinator.RequestRestart();
            return true;
        }
        finally
        {
            _changeGate.Release();
        }
    }
}
