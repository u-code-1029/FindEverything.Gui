using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FindEverything.Application.Catalog;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>A lazy, explicit view of the current disk, separate from indexed results.</summary>
public sealed class LiveDirectoryBrowser : UserControl
{
    private sealed class NodeState(string path) { public string Path { get; } = path; public int Offset; public bool Loaded; public bool Loading; public TreeViewItem? Error; }
    private sealed record LeafState(string Path, bool IsDirectory);
    private readonly ILiveDirectoryService _directories;
    private readonly IPathLauncher _paths;
    private readonly IAppLocalizer _localizer;
    private readonly TreeView _tree = new() { MinHeight = 120, MaxHeight = 360, BorderThickness = new(0) };
    private readonly Ui.TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly Ui.TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private CancellationTokenSource? _cancellation;
    private int _version;
    private string? _root;
    private bool _openingFile;

    public LiveDirectoryBrowser(ILiveDirectoryService directories, IAppLocalizer localizer, IPathLauncher? paths = null)
    {
        _directories = directories; _localizer = localizer;
        _paths = paths ?? new ExplorerPathLauncher(localizer);
        Width = 420; MaxWidth = 480;
        var panel = new StackPanel { Margin = new(4) };
        panel.Children.Add(_path);
        var help = new Ui.TextBlock { Text = L("Loc.LiveBrowser.Help", "현재 디스크 내용을 필요할 때만 읽습니다. 인덱스는 변경하지 않습니다."),
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
        help.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(help);
        var actions = new WrapPanel { Margin = new(0, 0, 0, 8) };
        actions.Children.Add(Button("Loc.LiveBrowser.Refresh", "새로고침", Ui.SymbolRegular.ArrowSync20, async () =>
        { if (_root is { } path) await OpenAsync(path); }));
        actions.Children.Add(Button("Loc.LiveBrowser.Copy", "경로 복사", Ui.SymbolRegular.Copy20, () =>
        { if (SelectedPath() is { } path) Copy(path); return Task.CompletedTask; }));
        actions.Children.Add(Button("Loc.LiveBrowser.Open", "탐색기에서 보기", Ui.SymbolRegular.FolderOpen20, () => OpenExplorerAsync(SelectedPath())));
        panel.Children.Add(actions);
        ScrollViewer.SetCanContentScroll(_tree, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_tree, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Auto);
        VirtualizingStackPanel.SetIsVirtualizing(_tree, true);
        VirtualizingStackPanel.SetVirtualizationMode(_tree, VirtualizationMode.Recycling);
        _tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnExpanded));
        _tree.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnNodeClick), true);
        _tree.KeyDown += OnTreeKeyDown;
        panel.Children.Add(_tree); panel.Children.Add(_message); Content = panel;
        Unloaded += (_, _) => Stop();
    }

    public async Task OpenAsync(string path)
    {
        Stop(); _root = path; _path.Text = path; _path.ToolTip = path;
        _tree.Items.Clear(); _message.Text = string.Empty;
        _cancellation = new();
        var root = new Ui.TreeViewItem { Header = Header(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } leaf ? leaf : path, true),
            Tag = new NodeState(path), ToolTip = path };
        root.Items.Add(new Ui.TreeViewItem { Header = "…", IsEnabled = false });
        AddContextMenu(root, path); _tree.Items.Add(root); root.IsExpanded = true;
        await LoadAsync(root, _version, _cancellation.Token);
    }

    public void Stop() { _version++; _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; }

    private async void OnExpanded(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not TreeViewItem { Tag: NodeState { Loaded: false, Loading: false } } node || _cancellation is null) return;
        args.Handled = true;
        await LoadAsync(node, _version, _cancellation.Token);
    }

    private async Task<bool> LoadAsync(TreeViewItem node, int version, CancellationToken token)
    {
        if (node.Tag is not NodeState state || state.Loading) return false;
        state.Loading = true;
        try
        {
            var page = await _directories.ReadPageAsync(state.Path, state.Offset, token);
            if (token.IsCancellationRequested || version != _version) return false;
            if (state.Offset == 0) node.Items.Clear();
            if (state.Error is { } previousError) node.Items.Remove(previousError);
            state.Error = null;
            foreach (var entry in page.Entries)
            {
                var child = new Ui.TreeViewItem { Header = Header(entry.Name, entry.IsDirectory), ToolTip = entry.FullPath };
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    child.Tag = new NodeState(entry.FullPath);
                    child.Items.Add(new Ui.TreeViewItem { Header = "…", IsEnabled = false });
                }
                else child.Tag = new LeafState(entry.FullPath, entry.IsDirectory);
                if (entry.IsReparsePoint) child.ToolTip = entry.FullPath + "\n" + L("Loc.LiveBrowser.Link", "링크·정션은 자동으로 펼치지 않습니다.");
                AddContextMenu(child, entry.FullPath, !entry.IsDirectory); node.Items.Add(child);
            }
            state.Offset = page.NextOffset; state.Loaded = true;
            if (page.HasMore)
            {
                var more = new Ui.TreeViewItem();
                Ui.Button? moreButton = null;
                moreButton = Button("Loc.LiveBrowser.More", "더 보기", Ui.SymbolRegular.ChevronDown20, async () =>
                {
                    if (version != _version || _cancellation is null || state.Loading || moreButton is null) return;
                    moreButton.IsEnabled = false;
                    if (await LoadAsync(node, version, _cancellation.Token)) node.Items.Remove(more);
                    else if (version == _version) moreButton.IsEnabled = true;
                });
                more.Header = moreButton;
                node.Items.Add(more);
            }
            if (state.Offset == 0) node.Items.Add(new Ui.TreeViewItem { Header = L("Loc.LiveBrowser.Empty", "비어 있는 폴더"), IsEnabled = false });
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (version != _version || token.IsCancellationRequested) return false;
            if (state.Offset == 0) node.Items.Clear();
            if (state.Error is { } previousError) node.Items.Remove(previousError);
            state.Error = new Ui.TreeViewItem { Header = exception.Message, IsEnabled = false, ToolTip = exception.Message };
            node.Items.Add(state.Error);
            return false;
        }
        finally { state.Loading = false; }
    }

    private StackPanel Header(string name, bool directory)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Ui.SymbolIcon(directory ? Ui.SymbolRegular.Folder20 : Ui.SymbolRegular.Document20) { Width = 18, Height = 18, Margin = new(0, 0, 8, 0) });
        header.Children.Add(new Ui.TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    private Ui.Button Button(string key, string fallback, Ui.SymbolRegular symbol, Func<Task> action)
    {
        var button = new Ui.Button { Content = L(key, fallback), Icon = new Ui.SymbolIcon(symbol), Margin = new(0, 0, 8, 0),
            Appearance = Ui.ControlAppearance.Transparent, Padding = new(8, 4, 8, 4) };
        button.Click += async (_, e) => { e.Handled = true; try { await action(); } catch (Exception exception) { _message.Text = exception.Message; } };
        return button;
    }
    private void AddContextMenu(TreeViewItem node, string path, bool isFile = false)
    {
        var menu = new ContextMenu();
        if (isFile)
        {
            var openFile = new Ui.MenuItem { Header = L("Loc.LiveBrowser.OpenFile", "기본 프로그램으로 열기") };
            openFile.Click += async (_, _) => await OpenFileAsync(path);
            menu.Items.Add(openFile);
        }
        var copy = new Ui.MenuItem { Header = L("Loc.LiveBrowser.Copy", "경로 복사") };
        copy.Click += (_, _) => Copy(path); menu.Items.Add(copy);
        var open = new Ui.MenuItem { Header = L("Loc.LiveBrowser.Open", "탐색기에서 보기") };
        open.Click += async (_, _) => await OpenExplorerAsync(path); menu.Items.Add(open); node.ContextMenu = menu;
    }
    private string? SelectedPath() => _tree.SelectedItem is TreeViewItem { Tag: NodeState state } ? state.Path
        : _tree.SelectedItem is TreeViewItem { Tag: LeafState leaf } ? leaf.Path : _root;

    private async void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject source
            || Ancestor<ButtonBase>(source) is not null || Ancestor<ScrollBar>(source) is not null
            || Ancestor<TreeViewItem>(source) is not { Tag: LeafState { IsDirectory: false } file }) return;
        e.Handled = true;
        await OpenFileAsync(file.Path);
    }

    private async void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _tree.SelectedItem is not TreeViewItem { Tag: LeafState { IsDirectory: false } file }) return;
        e.Handled = true;
        await OpenFileAsync(file.Path);
    }

    private async Task OpenFileAsync(string path)
    {
        if (_openingFile || _cancellation is null) return;
        _openingFile = true;
        var version = _version;
        try
        {
            await Task.Run(() =>
            {
                // Re-check the clicked leaf, because the live disk may have
                // changed since the page was read. Never treat a stale file
                // entry as a directory, and never touch an index here.
                if ((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.Directory) != 0)
                    throw new System.IO.FileNotFoundException(_localizer.Format("Loc.Path.Error.FileNotFound", "파일을 찾을 수 없습니다: {0}", path), path);
                _paths.OpenPath(path);
            });
            if (version == _version) _message.Text = string.Empty;
        }
        catch (Exception exception) { if (version == _version) _message.Text = exception.Message; }
        finally { _openingFile = false; }
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    private void Copy(string path) { try { Clipboard.SetText(path); } catch (Exception exception) { _message.Text = exception.Message; } }
    private async Task OpenExplorerAsync(string? path)
    {
        if (path is null) return;
        try { await Task.Run(() => _paths.ShowInFolder(path)); }
        catch (Exception exception) { _message.Text = exception.Message; }
    }
    private string L(string key, string fallback) => _localizer.Get(key, fallback);
}
