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
        viewModel.AssignmentPickerRequested += OnAssignmentPickerRequested;
    }

    private void OnAssignmentPickerRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => AssignmentPickerPanel.BringIntoView()));
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
