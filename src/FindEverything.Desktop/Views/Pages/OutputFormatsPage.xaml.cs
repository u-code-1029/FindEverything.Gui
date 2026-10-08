using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class OutputFormatsPage
{
    public OutputFormatsPage(OutputFormatsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
