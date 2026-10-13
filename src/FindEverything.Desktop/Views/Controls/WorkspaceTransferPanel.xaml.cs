using System.Windows;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Controls;

public partial class WorkspaceTransferPanel : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ExportOwnerProperty = DependencyProperty.Register(nameof(ExportOwner), typeof(object), typeof(WorkspaceTransferPanel));
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(object), typeof(WorkspaceTransferPanel));
    public static readonly DependencyProperty ShowProfileExportProperty = DependencyProperty.Register(nameof(ShowProfileExport), typeof(bool), typeof(WorkspaceTransferPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowPackageExportProperty = DependencyProperty.Register(nameof(ShowPackageExport), typeof(bool), typeof(WorkspaceTransferPanel), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowSourceSelectorProperty = DependencyProperty.Register(nameof(ShowSourceSelector), typeof(bool), typeof(WorkspaceTransferPanel), new PropertyMetadata(true));
    public object? ExportOwner { get => GetValue(ExportOwnerProperty); set => SetValue(ExportOwnerProperty, value); }
    public object? Profile { get => GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public bool ShowProfileExport { get => (bool)GetValue(ShowProfileExportProperty); set => SetValue(ShowProfileExportProperty, value); }
    public bool ShowPackageExport { get => (bool)GetValue(ShowPackageExportProperty); set => SetValue(ShowPackageExportProperty, value); }
    public bool ShowSourceSelector { get => (bool)GetValue(ShowSourceSelectorProperty); set => SetValue(ShowSourceSelectorProperty, value); }
    public WorkspaceTransferPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => TransferFlyout.Hide();
        TransferFlyout.Opened += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (!TransferFlyout.IsOpen) return;
                if (ShowSourceSelector) SourcePicker.Focus(); else WorkspaceOpenButton.Focus();
            }));
    }

    private void OnMenuKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        TransferFlyout.Hide();
        TransferMenuButton.Focus();
        e.Handled = true;
    }

    private void OnTransferActionClick(object sender, RoutedEventArgs e) => TransferFlyout.Hide();

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        TransferFlyout.ApplyTemplate();
        if (TransferFlyout.Template?.FindName("PART_Popup", TransferFlyout) is not System.Windows.Controls.Primitives.Popup popup) return;
        popup.PlacementTarget = TransferMenuButton;
        popup.StaysOpen = false;
        if (TransferFlyout.IsOpen) TransferFlyout.Hide(); else TransferFlyout.Show();
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return;
        var extension = System.IO.Path.GetExtension(paths[0]);
        e.Effects = extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".fepkg", StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not WorkspaceTransferViewModel model || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return;
        e.Handled = true;
        await model.OpenPathAsync(paths[0]);
    }
}
