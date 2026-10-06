using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed class MainWindowViewModel
{
    public string ApplicationTitle { get; } = "FindEverything";

    public IReadOnlyList<object> MenuItems { get; } =
    [
        new NavigationViewItem(
            "카탈로그",
            SymbolRegular.Home24,
            typeof(Views.Pages.CatalogPage))
        {
            NavigationCacheMode = NavigationCacheMode.Required,
        },
        new NavigationViewItem(
            "프로필",
            SymbolRegular.List24,
            typeof(Views.Pages.ProfilesPage))
        {
            NavigationCacheMode = NavigationCacheMode.Required,
        },
    ];

    public IReadOnlyList<object> FooterMenuItems { get; } =
    [
        new NavigationViewItem(
            "설정",
            SymbolRegular.Settings24,
            typeof(Views.Pages.SettingsPage))
        {
            NavigationCacheMode = NavigationCacheMode.Required,
        },
    ];
}
