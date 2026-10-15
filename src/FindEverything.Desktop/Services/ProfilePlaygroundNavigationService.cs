using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Pages;
using Wpf.Ui;

namespace FindEverything.Desktop.Services;

public interface IProfilePlaygroundNavigator
{
    bool Navigate(string profileId);
}

public interface IProfileEditorNavigator
{
    Task<bool> NavigateAsync(string profileId);
}

public sealed class ProfileEditorNavigator(
    INavigationService navigationService,
    Func<ProfilesViewModel> getEditor) : IProfileEditorNavigator
{
    public async Task<bool> NavigateAsync(string profileId)
    {
        if (!await getEditor().OpenForEditingAsync(profileId).ConfigureAwait(true)) return false;
        return navigationService.Navigate(typeof(ProfilesPage));
    }
}

public sealed class ProfilePlaygroundNavigator : IProfilePlaygroundNavigator
{
    private readonly INavigationService _navigationService;
    private readonly Func<ProfilePlaygroundViewModel> _getPlayground;

    public ProfilePlaygroundNavigator(INavigationService navigationService, Func<ProfilePlaygroundViewModel> getPlayground)
    {
        _navigationService = navigationService;
        _getPlayground = getPlayground;
    }

    public ProfilePlaygroundNavigator(INavigationService navigationService, ProfilePlaygroundViewModel playgroundViewModel)
        : this(navigationService, () => playgroundViewModel) { }

    public bool Navigate(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!_getPlayground().SelectProfile(profileId))
        {
            return false;
        }

        return _navigationService.Navigate(typeof(ProfilePlaygroundPage));
    }
}
