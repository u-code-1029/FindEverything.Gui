using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Pages;
using Wpf.Ui;

namespace FindEverything.Desktop.Services;

public interface IProfilePlaygroundNavigator
{
    bool Navigate(string profileId);
}

public sealed class ProfilePlaygroundNavigator(
    INavigationService navigationService,
    ProfilePlaygroundViewModel playgroundViewModel) : IProfilePlaygroundNavigator
{
    public bool Navigate(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!playgroundViewModel.SelectProfile(profileId))
        {
            return false;
        }

        return navigationService.Navigate(typeof(ProfilePlaygroundPage));
    }
}
