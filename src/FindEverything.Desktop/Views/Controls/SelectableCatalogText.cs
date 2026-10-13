using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FindEverything.Desktop.Behaviors;
using FindEverything.Desktop.ViewModels;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>A selectable, read-only cell with a lightweight highlighted idle view.</summary>
internal sealed class SelectableCatalogText : Grid
{
    internal Wpf.Ui.Controls.TextBox TextBox { get; }
    internal Wpf.Ui.Controls.TextBlock Highlight { get; }

    internal SelectableCatalogText(BindingBase displayBinding)
    {
        VerticalAlignment = VerticalAlignment.Center;
        MinWidth = 0;
        TextBox = new Wpf.Ui.Controls.TextBox
        {
            IsReadOnly = true, IsTextSelectionEnabled = true, ClearButtonEnabled = false,
            MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.NoWrap,
            Opacity = 0,
        };
        Highlight = new Wpf.Ui.Controls.TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        Highlight.SetBinding(TextHighlighting.DisplayTextProperty, displayBinding);
        // TextBox.Text defaults to TwoWay even when IsReadOnly is true. Bind
        // through the display property's OneWay bridge, never back to a DTO.
        TextBox.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding
        {
            Source = Highlight, Path = new PropertyPath(TextHighlighting.DisplayTextProperty), Mode = BindingMode.OneWay,
        });
        Highlight.SetBinding(TextHighlighting.HighlightTextProperty, new Binding("DataContext.FilterText")
        {
            Mode = BindingMode.OneWay,
            RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1),
        });
        SetBinding(ToolTipProperty, displayBinding);
        TextBox.GotKeyboardFocus += (_, _) => RefreshFocus();
        TextBox.LostKeyboardFocus += (_, _) => RefreshFocus();
        DataContextChanged += (_, _) => TextBox.Select(0, 0);
        Unloaded += (_, _) => TextBox.Select(0, 0);
        // Recycled cells need a menu owner, but allocating four menu items for
        // every visible cell penalizes scrolling even when no menu is opened.
        TextBox.ContextMenu = new ContextMenu();
        TextBox.ContextMenuOpening += (_, _) => PrepareContextMenu();
        Children.Add(TextBox);
        Children.Add(Highlight);
    }

    private void RefreshFocus()
    {
        TextBox.Opacity = TextBox.IsKeyboardFocusWithin ? 1 : 0;
        Highlight.Visibility = TextBox.IsKeyboardFocusWithin ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void PrepareContextMenu()
    {
        var menu = TextBox.ContextMenu!;
        if (menu.Items.Count > 0) return;
        var copy = new Wpf.Ui.Controls.MenuItem { Command = ApplicationCommands.Copy, CommandTarget = TextBox };
        copy.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Loc.Catalog.Text.Copy");
        var all = new Wpf.Ui.Controls.MenuItem();
        all.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Loc.Catalog.Text.SelectAll");
        all.Click += (_, _) => { TextBox.Focus(); TextBox.SelectAll(); };
        var open = new Wpf.Ui.Controls.MenuItem();
        open.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Loc.View.Files.Header.OpenFolder");
        open.Click += (_, _) => ExecuteRowAction(open: true);
        var path = new Wpf.Ui.Controls.MenuItem();
        path.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Loc.View.Files.Header.CopyFolderPath");
        path.Click += (_, _) => ExecuteRowAction(open: false);
        menu.Items.Add(copy);
        menu.Items.Add(all);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(path);
    }

    private void ExecuteRowAction(bool open)
    {
        var grid = FindAncestor<DataGrid>(this);
        if (grid?.DataContext is not CatalogViewModel viewModel || DataContext is not CatalogItemViewModel item) return;
        var command = open ? viewModel.OpenItemCommand : viewModel.CopyItemFolderPathCommand;
        if (command.CanExecute(item)) command.Execute(item);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
