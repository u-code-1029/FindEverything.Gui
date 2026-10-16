using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.Views.Controls;

namespace FindEverything.Desktop.Views.Pages;

public partial class ProfilesPage
{
    private ProfileEditorList? _activeEditorList;
    internal ProfileEditorList? ActiveEditorList => _activeEditorList;
    internal ScrollViewer? EditorScrollViewer => _activeEditorList?.ScrollViewer;

    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PageActivation.Attach(this);
        viewModel.AssignmentPickerRequested += OnAssignmentPickerRequested;
        viewModel.EditorStepChanged += OnEditorStepChanged;
        viewModel.ValidationIssuesRequested += OnEditorStepChanged;
        viewModel.EntryPathEditorRequested += OnEntryPathEditorRequested;
    }

    private void OnEntryPathEditorRequested(ProfileEntryPathDraftViewModel row)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_activeEditorList?.Items.OfType<ProfileEditorEntry>()
                .FirstOrDefault(entry => ReferenceEquals(entry.Value, row)) is not { } entry) return;
            _activeEditorList.ScrollIntoView(entry);
            _activeEditorList.UpdateLayout();
            var container = _activeEditorList.ItemContainerGenerator.ContainerFromItem(entry) as System.Windows.FrameworkElement;
            if (container is not null) FindEntryPathTextBox(container)?.Focus();
        }));
    }

    private static System.Windows.Controls.TextBox? FindEntryPathTextBox(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBox { Name: "ProfileEntryPathInput" } input) return input;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindEntryPathTextBox(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private void OnProfileWorkspaceSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => ApplyWorkspaceLayout();

    private void OnAssignmentPickerVisibilityChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) => ApplyWorkspaceLayout();

    private void ApplyWorkspaceLayout()
    {
        if (ProfileStepButtons is null) return;
        ProfileStepButtons.Columns = 3;
    }

    private void OnOptionalStepsClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnEditorStepContentLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _activeEditorList = sender as ProfileEditorList;
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
            new Action(() => EditorScrollViewer?.ScrollToTop()));
    }

    private void OnAssignmentPickerRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => _activeEditorList?.ScrollToSection("AssignmentPicker")));
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
