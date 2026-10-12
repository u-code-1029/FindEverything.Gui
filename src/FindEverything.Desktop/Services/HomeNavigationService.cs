using FindEverything.Desktop.Views.Pages;
using Wpf.Ui;

namespace FindEverything.Desktop.Services;

public interface IHomeNavigationService
{
    bool Navigate(Type pageType);
}

public sealed class HomeNavigationService(INavigationService navigationService)
    : IHomeNavigationService
{
    private static readonly HashSet<Type> AllowedPages =
    [
        typeof(FilesPage),
        typeof(CatalogPage),
        typeof(ProfilesPage),
        typeof(ProfilePlaygroundPage),
        typeof(OutputFormatsPage),
    ];

    public bool Navigate(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        return AllowedPages.Contains(pageType) && navigationService.Navigate(pageType);
    }
}
