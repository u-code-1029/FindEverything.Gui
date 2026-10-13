using System.Windows;
using FindEverything.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Views.Dialogs;

public partial class SelectionImportDialog : FluentWindow
{
    public SelectionImportDialog(SelectionImportViewModel viewModel)
    {
        InitializeComponent(); SetResourceReference(StyleProperty, typeof(FluentWindow)); DataContext = viewModel;
        viewModel.ApplyRequested += OnApply; viewModel.CloseRequested += OnClose;
        Closed += (_, _) => { viewModel.ApplyRequested -= OnApply; viewModel.CloseRequested -= OnClose; };
    }
    private void OnApply(object? sender, EventArgs args) { DialogResult = true; Close(); }
    private void OnClose(object? sender, EventArgs args) => Close();
}
