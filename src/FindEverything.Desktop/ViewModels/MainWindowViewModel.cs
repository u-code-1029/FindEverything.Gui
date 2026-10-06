using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public sealed class MainWindowViewModel
{
    public string ApplicationTitle { get; } = "FindEverything";

    public IReadOnlyList<object> MenuItems { get; } =
    [
        new NavigationViewItem(
            "파일 찾기",
            SymbolRegular.Search24,
            typeof(Views.Pages.FilesPage))
        {
            NavigationCacheMode = NavigationCacheMode.Required,
        },
        new NavigationViewItem(
            "구조화 보기",
            SymbolRegular.TableSimple24,
            typeof(Views.Pages.CatalogPage))
        {
            NavigationCacheMode = NavigationCacheMode.Required,
        },
        new NavigationViewItem(
            "프로필",
            SymbolRegular.DocumentEdit24,
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
