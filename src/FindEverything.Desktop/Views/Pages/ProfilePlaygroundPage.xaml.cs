using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class ProfilePlaygroundPage
{
    public ProfilePlaygroundPage(ProfilePlaygroundViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
