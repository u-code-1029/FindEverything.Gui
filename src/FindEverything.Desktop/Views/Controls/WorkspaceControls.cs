using System.Windows;
using System.Windows.Controls;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>A shared page heading; detailed guidance is available without occupying the workspace.</summary>
public sealed class PageHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(PageHeader), new PropertyMetadata(false));

    public PageHeader() => SetResourceReference(StyleProperty, "WorkspacePageHeaderStyle");
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); private set => SetValue(IsCompactProperty, value); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        IsCompact = sizeInfo.NewSize.Width < 600;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("HelpButton") is Wpf.Ui.Controls.Button button)
        {
            button.Click += (_, _) =>
            {
                var tip = new ToolTip
                {
                    Content = new Wpf.Ui.Controls.TextBlock { Text = Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
                    PlacementTarget = button,
                    StaysOpen = false,
                };
                button.ToolTip = tip;
                tip.IsOpen = true;
            };
        }
    }
}

/// <summary>The same compact command surface on every workspace.</summary>
public sealed class CommandStrip : ContentControl
{
    public CommandStrip() => SetResourceReference(StyleProperty, "WorkspaceCommandStripStyle");
}

/// <summary>A non-interactive badge or highlight; it never introduces button semantics.</summary>
public sealed class DisplayBadge : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(DisplayBadge), new PropertyMetadata(new CornerRadius(4)));

    public DisplayBadge() => SetResourceReference(StyleProperty, "WorkspaceDisplayBadgeStyle");
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
}

/// <summary>Separates selection management from output actions without shrinking editors.</summary>
public sealed class PurposeFooterPanel : Panel
{
    private bool _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 2000 : availableSize.Width;
        foreach (UIElement child in InternalChildren) child.Measure(new Size(width, double.PositiveInfinity));
        if (InternalChildren.Count != 2) return new Size();
        var left = InternalChildren[0].DesiredSize;
        var right = InternalChildren[1].DesiredSize;
        var gap = width < 1040 ? 8 : 16;
        _stacked = left.Width + right.Width + gap > width;
        return new Size(Math.Min(width, _stacked ? Math.Max(left.Width, right.Width) : left.Width + right.Width + gap),
            _stacked ? left.Height + right.Height + 8 : Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var left = InternalChildren[0];
        var right = InternalChildren[1];
        left.Arrange(new Rect(0, 0, Math.Min(finalSize.Width, left.DesiredSize.Width), left.DesiredSize.Height));
        var rightWidth = Math.Min(finalSize.Width, right.DesiredSize.Width);
        right.Arrange(new Rect(finalSize.Width - rightWidth, _stacked ? left.DesiredSize.Height + 8 : 0,
            rightWidth, right.DesiredSize.Height));
        return finalSize;
    }
}

/// <summary>A responsive PowerToys-style settings row with a stretching editor.</summary>
public sealed class SettingRowCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRowCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRowCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(SettingRowCard), new PropertyMetadata(false));

    public SettingRowCard() => SetResourceReference(StyleProperty, "WorkspaceSettingRowStyle");
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); private set => SetValue(IsCompactProperty, value); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        IsCompact = sizeInfo.NewSize.Width < 900;
    }
}

/// <summary>Page gutters adapt to the actual workspace, independently of the navigation pane.</summary>
public sealed class WorkspacePageFrame : Grid
{
    public WorkspacePageFrame()
    {
        SetResourceReference(MarginProperty, "PagePadding");
        SizeChanged += (_, _) =>
        {
            var inset = ActualWidth < 900 ? 16 : 24;
            if (Margin.Left != inset)
            {
                var token = TryFindResource(inset == 16 ? "CompactPagePadding" : "PagePadding") is Thickness padding
                    ? padding : new Thickness(inset, 0, inset, 16);
                Margin = new Thickness(token.Left, Margin.Top, token.Right, Margin.Bottom);
            }
        };
    }
}

/// <summary>Two useful work areas become keyboard-accessible tabs in narrow windows.</summary>
public sealed class SplitWorkspace : Grid
{
    public static readonly DependencyProperty PrimaryContentProperty = DependencyProperty.Register(
        nameof(PrimaryContent), typeof(FrameworkElement), typeof(SplitWorkspace), new PropertyMetadata(null, OnContentChanged));
    public static readonly DependencyProperty SecondaryContentProperty = DependencyProperty.Register(
        nameof(SecondaryContent), typeof(FrameworkElement), typeof(SplitWorkspace), new PropertyMetadata(null, OnContentChanged));
    public static readonly DependencyProperty PrimaryTitleProperty = DependencyProperty.Register(
        nameof(PrimaryTitle), typeof(string), typeof(SplitWorkspace), new PropertyMetadata(string.Empty, OnContentChanged));
    public static readonly DependencyProperty SecondaryTitleProperty = DependencyProperty.Register(
        nameof(SecondaryTitle), typeof(string), typeof(SplitWorkspace), new PropertyMetadata(string.Empty, OnContentChanged));
    public static readonly DependencyProperty PrimaryRatioProperty = DependencyProperty.Register(
        nameof(PrimaryRatio), typeof(double), typeof(SplitWorkspace), new PropertyMetadata(0.6, OnContentChanged));
    public static readonly DependencyProperty KeepSecondaryVisibleProperty = DependencyProperty.Register(
        nameof(KeepSecondaryVisible), typeof(bool), typeof(SplitWorkspace), new PropertyMetadata(false, OnContentChanged));
    private bool? _compact;
    private TabItem? _primaryTab;
    private TabItem? _secondaryTab;

    public FrameworkElement? PrimaryContent { get => (FrameworkElement?)GetValue(PrimaryContentProperty); set => SetValue(PrimaryContentProperty, value); }
    public FrameworkElement? SecondaryContent { get => (FrameworkElement?)GetValue(SecondaryContentProperty); set => SetValue(SecondaryContentProperty, value); }
    public string PrimaryTitle { get => (string)GetValue(PrimaryTitleProperty); set => SetValue(PrimaryTitleProperty, value); }
    public string SecondaryTitle { get => (string)GetValue(SecondaryTitleProperty); set => SetValue(SecondaryTitleProperty, value); }
    public double PrimaryRatio { get => (double)GetValue(PrimaryRatioProperty); set => SetValue(PrimaryRatioProperty, value); }
    public bool KeepSecondaryVisible { get => (bool)GetValue(KeepSecondaryVisibleProperty); set => SetValue(KeepSecondaryVisibleProperty, value); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var compact = sizeInfo.NewSize.Width < 900;
        if (_compact != compact) Rebuild(compact);
    }

    private static void OnContentChanged(DependencyObject source, DependencyPropertyChangedEventArgs _) =>
        ((SplitWorkspace)source).Rebuild(((SplitWorkspace)source).ActualWidth < 900);

    private void Rebuild(bool compact)
    {
        _compact = compact;
        if (_primaryTab is not null) _primaryTab.Content = null;
        if (_secondaryTab is not null) _secondaryTab.Content = null;
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        if (PrimaryContent is not null) { SetRow(PrimaryContent, 0); SetColumn(PrimaryContent, 0); }
        if (SecondaryContent is not null) { SetRow(SecondaryContent, 0); SetColumn(SecondaryContent, 0); }
        _primaryTab = null;
        _secondaryTab = null;
        if (compact && KeepSecondaryVisible)
        {
            // Editing an expression and reading its effect is one task. Keep a
            // bounded preview visible without requiring a tab round trip.
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 96 });
            if (PrimaryContent is not null) Children.Add(PrimaryContent);
            if (SecondaryContent is not null) { SetRow(SecondaryContent, 2); Children.Add(SecondaryContent); }
            return;
        }
        if (compact)
        {
            _primaryTab = new Wpf.Ui.Controls.TabViewItem { Header = PrimaryTitle, Content = PrimaryContent };
            _secondaryTab = new Wpf.Ui.Controls.TabViewItem { Header = SecondaryTitle, Content = SecondaryContent };
            var tabs = new Wpf.Ui.Controls.TabView { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            tabs.Items.Add(_primaryTab);
            tabs.Items.Add(_secondaryTab);
            Children.Add(tabs);
            return;
        }

        var ratio = Math.Clamp(PrimaryRatio, 0.1, 0.9);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        if (PrimaryContent is not null) { SetColumn(PrimaryContent, 0); Children.Add(PrimaryContent); }
        if (SecondaryContent is not null) { SetColumn(SecondaryContent, 2); Children.Add(SecondaryContent); }
    }
}
