using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Behaviors;

namespace FindEverything.Desktop.Views.Pages;

public partial class ProfilePlaygroundPage
{
    public ProfilePlaygroundPage(ProfilePlaygroundViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PageActivation.Attach(this);
    }
}
