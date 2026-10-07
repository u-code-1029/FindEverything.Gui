namespace FindEverything.Desktop.ViewModels;

public sealed class MainWindowViewModel(
    CatalogViewModel catalog,
    ScanConsoleViewModel scanConsole)
{
    public string ApplicationTitle { get; } = "FindEverything";

    public CatalogViewModel Catalog { get; } = catalog;

    public ScanConsoleViewModel ScanConsole { get; } = scanConsole;
}
