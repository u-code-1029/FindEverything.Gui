using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Behaviors;

namespace FindEverything.Desktop.Views.Pages;

public partial class ProfilesPage
{
    private Grid? _guidedWorkspace;
    private System.Windows.FrameworkElement? _assignmentPicker;
    private bool? _compact;

    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PageActivation.Attach(this);
        viewModel.AssignmentPickerRequested += OnAssignmentPickerRequested;
        viewModel.EditorStepChanged += OnEditorStepChanged;
        viewModel.ValidationIssuesRequested += OnEditorStepChanged;
    }

    private void OnProfileWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void OnAssignmentPickerVisibilityChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (ProfileStepButtons is null) return;
        var compact = ProfilePageLayout.ActualWidth < 900;
        if (_compact != compact) { ProfileStepButtons.Columns = compact ? 2 : 3; _compact = compact; }
        if (_guidedWorkspace is not { IsLoaded: true } workspace || _assignmentPicker is not { IsLoaded: true } picker) return;
        var showPicker = picker.Visibility == System.Windows.Visibility.Visible;
        Grid.SetColumn(picker, compact ? 0 : 2);
        Grid.SetRow(picker, compact ? 1 : 0);
        Grid.SetColumnSpan(picker, compact ? 3 : 1);
        workspace.ColumnDefinitions[0].Width = new System.Windows.GridLength(showPicker && !compact ? 0.65 : 1, System.Windows.GridUnitType.Star);
        workspace.ColumnDefinitions[1].Width = new System.Windows.GridLength(showPicker && !compact ? 16 : 0);
        workspace.ColumnDefinitions[2].Width = new System.Windows.GridLength(showPicker && !compact ? 0.35 : 0, System.Windows.GridUnitType.Star);
        picker.Margin = compact ? new System.Windows.Thickness(0, 12, 0, 0) : new System.Windows.Thickness(0);
    }

    private void OnEditorStepContentLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _guidedWorkspace = FindEditorElement<Grid>("GuidedWorkspace");
        _assignmentPicker = FindEditorElement<System.Windows.FrameworkElement>("AssignmentPickerPanel");
        ApplyWorkspaceLayout();
    }

    /// <summary>Finds only instantiated step controls; never creates another step or enumerates data items.</summary>
    internal T? FindEditorElement<T>(string name) where T : System.Windows.DependencyObject
    {
        T? Find(System.Windows.DependencyObject parent)
        {
            if (parent is T match && parent is System.Windows.FrameworkElement { Name: var elementName } && elementName == name) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (Find(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
            return null;
        }
        return ProfileEditorStepHost is null ? null : Find(ProfileEditorStepHost);
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
            new Action(() => _assignmentPicker?.BringIntoView()));
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
