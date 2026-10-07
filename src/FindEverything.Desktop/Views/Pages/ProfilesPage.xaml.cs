using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Pages;

public partial class ProfilesPage
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnInsertTemplateFieldClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement
            {
                DataContext: ProfileFieldDraftViewModel field,
            } ||
            DataContext is not ProfilesViewModel viewModel)
        {
            return;
        }

        var current = ProfilePathTemplateTextBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(
            ProfilePathTemplateTextBox.SelectionStart,
            0,
            current.Length);
        var selectionLength = Math.Clamp(
            ProfilePathTemplateTextBox.SelectionLength,
            0,
            current.Length - selectionStart);
        var updated = current.Remove(selectionStart, selectionLength)
            .Insert(selectionStart, field.TemplateToken);

        viewModel.DraftPathTemplate = updated;
        ProfilePathTemplateTextBox.Focus();
        ProfilePathTemplateTextBox.SelectionStart = selectionStart + field.TemplateToken.Length;
        ProfilePathTemplateTextBox.SelectionLength = 0;
    }

    private void OnNestedDataGridPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is not DataGrid dataGrid || e.Delta == 0)
        {
            return;
        }

        var parent = VisualTreeHelper.GetParent(dataGrid);
        while (parent is not null && parent is not ScrollViewer)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is not ScrollViewer scrollViewer)
        {
            return;
        }

        // A DataGrid's internal ScrollViewer consumes wheel input even when its
        // vertical scrollbar is disabled. Re-raise the gesture on the outer
        // viewer so WPF keeps the user's system wheel-scrolling preferences.
        e.Handled = true;
        scrollViewer.RaiseEvent(new MouseWheelEventArgs(
            e.MouseDevice,
            e.Timestamp,
            e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
        });
    }
}
