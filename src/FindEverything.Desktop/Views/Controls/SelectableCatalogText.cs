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
    private readonly Wpf.Ui.Controls.TextBox _textBox;
    private bool _nativeEditorPrepared;
    // Explicit programmatic access has the same semantics as user input.
    // Normal result rendering never calls this accessor.
    internal Wpf.Ui.Controls.TextBox TextBox { get { PrepareNativeEditor(); return _textBox; } }
    internal bool IsNativeEditorPrepared => _nativeEditorPrepared;
    internal Wpf.Ui.Controls.TextBlock Highlight { get; }

    internal SelectableCatalogText(BindingBase displayBinding)
    {
        VerticalAlignment = VerticalAlignment.Center;
        MinWidth = 0;
        _textBox = new Wpf.Ui.Controls.TextBox
        {
            IsReadOnly = true, IsTextSelectionEnabled = true, ClearButtonEnabled = false,
            MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.NoWrap,
            Opacity = 0,
        };
        _textBox.SetResourceReference(StyleProperty, "CatalogReadOnlyTextBoxStyle");
        _textBox.PlaceholderEnabled = false;
        Highlight = new Wpf.Ui.Controls.TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        Highlight.SetBinding(TextHighlighting.DisplayTextProperty, displayBinding);
        // TextBox.Text defaults to TwoWay even when IsReadOnly is true. Bind
        // through the display property's OneWay bridge, never back to a DTO.
        _textBox.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding
        {
            Source = Highlight, Path = new PropertyPath(TextHighlighting.DisplayTextProperty), Mode = BindingMode.OneWay,
        });
        Highlight.SetBinding(TextHighlighting.HighlightTextProperty, new Binding("DataContext.AppliedHighlightQuery")
        {
            Mode = BindingMode.OneWay,
            RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1),
        });
        SetBinding(ToolTipProperty, displayBinding);
        _textBox.PreviewGotKeyboardFocus += (_, _) => PrepareNativeEditor();
        _textBox.GotKeyboardFocus += (_, _) => RefreshFocus();
        _textBox.LostKeyboardFocus += (_, _) => RefreshFocus();
        _textBox.MouseEnter += (_, _) => PrepareNativeEditor();
        _textBox.StylusEnter += (_, _) => PrepareNativeEditor();
        // The collapsed idle text view must be laid out before the original
        // native down event reaches TextEditorMouse/TextEditorTyping. Retain
        // that event and its capture/drag behavior; never synthesize a replay.
        _textBox.PreviewMouseLeftButtonDown += (_, _) => PreparePointerInput();
        _textBox.PreviewMouseRightButtonDown += (_, _) => PrepareNativeEditor();
        _textBox.PreviewStylusDown += (_, _) => PreparePointerInput();
        _textBox.PreviewTouchDown += (_, _) => PreparePointerInput();
        DataContextChanged += (_, _) => _textBox.Select(0, 0);
        Unloaded += (_, _) => _textBox.Select(0, 0);
        // Recycled cells need a menu owner, but allocating four menu items for
        // every visible cell penalizes scrolling even when no menu is opened.
        _textBox.ContextMenu = new ContextMenu();
        _textBox.ContextMenuOpening += (_, _) => PrepareContextMenu();
        Children.Add(_textBox);
        Children.Add(Highlight);
    }

    private void RefreshFocus()
    {
        _textBox.Opacity = _textBox.IsKeyboardFocusWithin ? 1 : 0;
        Highlight.Visibility = _textBox.IsKeyboardFocusWithin ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PrepareNativeEditor()
    {
        if (_nativeEditorPrepared) return;
        if (TryFindResource("CatalogReadOnlyTextBoxActiveTemplate") is not ControlTemplate template) return;
        _nativeEditorPrepared = true;
        _textBox.Template = template;
        _textBox.ApplyTemplate();
    }

    private void PreparePointerInput()
    {
        PrepareNativeEditor();
        _textBox.Focus();
        _textBox.UpdateLayout();
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
