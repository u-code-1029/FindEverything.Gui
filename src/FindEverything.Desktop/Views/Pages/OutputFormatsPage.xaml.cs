using System.Windows;
using System.Windows.Controls;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Behaviors;

namespace FindEverything.Desktop.Views.Pages;

public partial class OutputFormatsPage
{
    public OutputFormatsPage(OutputFormatsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PageActivation.Attach(this);
    }

    private void OnInsertTokenClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OutputFormatsViewModel viewModel
            || sender is not FrameworkElement { DataContext: OutputFormatTokenChoice choice }) return;

        var caret = viewModel.InsertTokenAtSelection(choice, TemplateTextBox.SelectionStart, TemplateTextBox.SelectionLength);
        TemplateTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        TemplateTextBox.Focus();
        TemplateTextBox.Select(caret, 0);
    }
}
