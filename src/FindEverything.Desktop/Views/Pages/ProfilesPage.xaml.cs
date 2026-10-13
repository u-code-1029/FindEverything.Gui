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
        viewModel.EditorStepChanged += OnEditorStepChanged;
        viewModel.ValidationIssuesRequested += OnEditorStepChanged;
    }

    private void OnProfileWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void OnAssignmentPickerVisibilityChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (ProfileStepButtons is null || GuidedWorkspace is null || AssignmentPickerPanel is null) return;
        var compact = ProfilePageLayout.ActualWidth < 900;
        ProfileStepButtons.Columns = compact ? 2 : 3;
        var showPicker = AssignmentPickerPanel.Visibility == System.Windows.Visibility.Visible;
        System.Windows.Controls.Grid.SetColumn(AssignmentPickerPanel, compact ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(AssignmentPickerPanel, compact ? 1 : 0);
        System.Windows.Controls.Grid.SetColumnSpan(AssignmentPickerPanel, compact ? 3 : 1);
        GuidedEditorColumn.Width = new System.Windows.GridLength(showPicker && !compact ? 0.65 : 1, System.Windows.GridUnitType.Star);
        GuidedGapColumn.Width = new System.Windows.GridLength(showPicker && !compact ? 16 : 0);
        GuidedChoicesColumn.Width = new System.Windows.GridLength(showPicker && !compact ? 0.35 : 0, System.Windows.GridUnitType.Star);
        AssignmentPickerPanel.Margin = compact ? new System.Windows.Thickness(0, 12, 0, 0) : new System.Windows.Thickness(0);
    }

    private void OnEditorStepChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => ProfileEditorScrollViewer.ScrollToTop()));
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
