using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FindEverything.Desktop.Behaviors;

/// <summary>
/// Measures only the selected label, retaining the application's ComboBox template.
/// Use ContentAwareComboBoxStyle without a fixed Width/MinWidth/MaxWidth. Put toolbar
/// selectors in a bounded Grid column; a horizontal StackPanel cannot allocate space
/// between growing siblings. SelectedText can describe a non-textual custom template.
/// </summary>
public static class ComboBoxContentSizing
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ComboBoxContentSizing), new PropertyMetadata(false, OnEnabledChanged));
    public static readonly DependencyProperty SelectedTextProperty = DependencyProperty.RegisterAttached(
        "SelectedText", typeof(string), typeof(ComboBoxContentSizing), new PropertyMetadata(null, OnSizingChanged));
    public static readonly DependencyProperty MinimumWidthProperty = DependencyProperty.RegisterAttached(
        "MinimumWidth", typeof(double), typeof(ComboBoxContentSizing), new PropertyMetadata(96d, OnSizingChanged),
        value => value is double width && double.IsFinite(width) && width >= 0);
    private static readonly DependencyPropertyKey DesiredMinimumWidthPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "DesiredMinimumWidth", typeof(double), typeof(ComboBoxContentSizing), new PropertyMetadata(0d));
    public static readonly DependencyProperty DesiredMinimumWidthProperty = DesiredMinimumWidthPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey AvailableWidthPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "AvailableWidth", typeof(double), typeof(ComboBoxContentSizing), new PropertyMetadata(double.PositiveInfinity));
    public static readonly DependencyProperty AvailableWidthProperty = AvailableWidthPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey FullSelectedTextPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "FullSelectedText", typeof(string), typeof(ComboBoxContentSizing), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty FullSelectedTextProperty = FullSelectedTextPropertyKey.DependencyProperty;
    private static readonly DependencyProperty AttachmentProperty = DependencyProperty.RegisterAttached(
        "Attachment", typeof(Attachment), typeof(ComboBoxContentSizing), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    public static string? GetSelectedText(DependencyObject value) => (string?)value.GetValue(SelectedTextProperty);
    public static void SetSelectedText(DependencyObject value, string? text) => value.SetValue(SelectedTextProperty, text);
    public static double GetMinimumWidth(DependencyObject value) => (double)value.GetValue(MinimumWidthProperty);
    public static void SetMinimumWidth(DependencyObject value, double width) => value.SetValue(MinimumWidthProperty, width);
    public static double GetDesiredMinimumWidth(DependencyObject value) => (double)value.GetValue(DesiredMinimumWidthProperty);
    public static double GetAvailableWidth(DependencyObject value) => (double)value.GetValue(AvailableWidthProperty);
    public static string GetFullSelectedText(DependencyObject value) => (string)value.GetValue(FullSelectedTextProperty);
    internal static ToolTip? ShowFullSelectedText(ComboBox combo) =>
        (combo.GetValue(AttachmentProperty) as Attachment)?.ShowFullSelectedText();

    private static void OnEnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not ComboBox combo) return;
        (combo.GetValue(AttachmentProperty) as Attachment)?.Dispose();
        combo.ClearValue(AttachmentProperty);
        if ((bool)args.NewValue) combo.SetValue(AttachmentProperty, new Attachment(combo));
    }

    private static void OnSizingChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) =>
        (value.GetValue(AttachmentProperty) as Attachment)?.QueueUpdate(measure: true);

    private sealed class Attachment : DependencyObject, IDisposable
    {
        private static readonly DependencyProperty LabelValueProperty = DependencyProperty.Register(
            "LabelValue", typeof(object), typeof(Attachment), new PropertyMetadata(null,
                (value, _) => ((Attachment)value).OnLabelValueChanged()));
        private static readonly DependencyPropertyDescriptor[] WatchedProperties = new[]
        {
            Control.FontFamilyProperty, Control.FontSizeProperty, Control.FontWeightProperty,
            Control.FontStyleProperty, Control.FontStretchProperty, Control.PaddingProperty,
            Control.BorderThicknessProperty, FrameworkElement.FlowDirectionProperty, FrameworkElement.LanguageProperty,
            Control.TemplateProperty, ItemsControl.DisplayMemberPathProperty, ItemsControl.ItemTemplateProperty,
            ItemsControl.ItemTemplateSelectorProperty, FrameworkElement.ToolTipProperty
        }.Select(property => DependencyPropertyDescriptor.FromProperty(property, typeof(ComboBox))!).ToArray();
        private static readonly DependencyPropertyDescriptor TextDescriptor =
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))!;
        private readonly ComboBox _combo;
        private readonly List<FrameworkElement> _ancestors = [];
        private readonly List<TextBlock> _selectedTextBlocks = [];
        private readonly ToolTip _emptyTip = new() { Content = string.Empty };
        private Window? _window;
        private ContentPresenter? _presenter;
        private ToolTip? _fullTextTip;
        private bool _isClipped;
        private bool _updatingTooltip;
        private DispatcherOperation? _pending;
        private bool _active;
        private bool _measurementDirty = true;
        private double _desiredWidth;
        private string _text = string.Empty;

        internal Attachment(ComboBox combo)
        {
            _combo = combo;
            combo.Loaded += OnLoaded;
            combo.Unloaded += OnUnloaded;
            if (combo.IsLoaded) Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) => BindSelectedValue();
        private void OnControlPropertyChanged(object? sender, EventArgs args)
        {
            if (!_updatingTooltip) BindSelectedValue();
        }
        private void OnSelectedTextChanged(object? sender, EventArgs args) => QueueUpdate(measure: true);
        private void OnLabelValueChanged()
        {
            ClearSelectedVisuals();
            QueueUpdate(measure: true);
        }
        private void OnSizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (args.WidthChanged) QueueUpdate(measure: false);
        }
        private void OnDpiChanged(object sender, DpiChangedEventArgs args) => QueueUpdate(measure: true);
        private void OnToolTipOpening(object sender, ToolTipEventArgs args)
        {
            if (ShowFullSelectedText() is not null || ReferenceEquals(_combo.ToolTip, _emptyTip)) args.Handled = true;
        }
        internal ToolTip? ShowFullSelectedText()
        {
            if (!_active || !_isClipped || _fullTextTip is null) return null;
            _fullTextTip.PlacementTarget = _combo;
            _fullTextTip.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _fullTextTip.IsOpen = true;
            return _fullTextTip;
        }
        private void CloseFullTextTip(object? sender, EventArgs args)
        {
            if (_fullTextTip is not null) _fullTextTip.IsOpen = false;
        }
        private void OnPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape) CloseFullTextTip(sender, args);
        }

        private void Attach()
        {
            if (_active) return;
            _active = true;
            _combo.SelectionChanged += OnSelectionChanged;
            _combo.SizeChanged += OnSizeChanged;
            _combo.ToolTipOpening += OnToolTipOpening;
            _combo.MouseLeave += CloseFullTextTip;
            _combo.LostKeyboardFocus += CloseFullTextTip;
            _combo.DropDownOpened += CloseFullTextTip;
            _combo.PreviewKeyDown += OnPreviewKeyDown;
            foreach (var descriptor in WatchedProperties) descriptor.AddValueChanged(_combo, OnControlPropertyChanged);
            for (var parent = VisualTreeHelper.GetParent(_combo); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement element)
                {
                    _ancestors.Add(element);
                    element.SizeChanged += OnSizeChanged;
                }
            _window = Window.GetWindow(_combo);
            if (_window is not null)
            {
                _window.DpiChanged += OnDpiChanged;
                _window.Deactivated += CloseFullTextTip;
            }
            BindSelectedValue();
        }

        private void BindSelectedValue()
        {
            ClearSelectedVisuals();
            BindingOperations.ClearBinding(this, LabelValueProperty);
            if (_combo.SelectedItem is { } item)
            {
                var path = !string.IsNullOrEmpty(_combo.DisplayMemberPath) ? _combo.DisplayMemberPath
                    : item is ComboBoxItem ? nameof(ComboBoxItem.Content) : string.Empty;
                BindingOperations.SetBinding(this, LabelValueProperty, new Binding(path) { Source = item, Mode = BindingMode.OneWay });
            }
            else SetValue(LabelValueProperty, null);
            QueueUpdate(measure: true);
        }

        internal void QueueUpdate(bool measure)
        {
            _measurementDirty |= measure;
            if (!_active || _pending is not null) return;
            _pending = _combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Update));
        }

        private void Update()
        {
            _pending = null;
            if (!_active) return;
            _combo.ApplyTemplate();
            DiscoverSelectedVisuals();
            if (_measurementDirty)
            {
                _measurementDirty = false;
                _text = ReadText();
                _combo.SetValue(FullSelectedTextPropertyKey, _text);
                // A pathological label still gets a bounded control and the full
                // tooltip. Do not shape megabytes of text just to choose a width.
                var textWidth = _text.Length > 4096 ? double.PositiveInfinity : new FormattedText(
                    _text, CultureInfo.CurrentUICulture, _combo.FlowDirection,
                    new Typeface(_combo.FontFamily, _combo.FontStyle, _combo.FontWeight, _combo.FontStretch),
                    _combo.FontSize, Brushes.Transparent, VisualTreeHelper.GetDpi(_combo).PixelsPerDip).WidthIncludingTrailingWhitespace;
                _desiredWidth = Math.Max(GetMinimumWidth(_combo), Math.Ceiling(textWidth +
                    _combo.Padding.Left + _combo.Padding.Right + _combo.BorderThickness.Left + _combo.BorderThickness.Right + 36));
            }
            var available = FindAvailableWidth();
            SetWidth(AvailableWidthPropertyKey, available);
            SetWidth(DesiredMinimumWidthPropertyKey, Math.Min(_desiredWidth, available));
            UpdateFullTextTip(_desiredWidth > Math.Min(available, _combo.ActualWidth > 0 ? _combo.ActualWidth : available) + 1);
        }

        private void SetWidth(DependencyPropertyKey key, double value)
        {
            if (Math.Abs((double)_combo.GetValue(key.DependencyProperty) - value) > .5)
                _combo.SetValue(key, value);
        }

        private double FindAvailableWidth()
        {
            var available = Math.Max(1, SystemParameters.WorkArea.Width - 32);
            FrameworkElement child = _combo;
            var inset = child.Margin.Left + child.Margin.Right;
            foreach (var parent in _ancestors)
            {
                var padding = parent is Border padded
                    ? padded.Padding.Left + padded.Padding.Right + padded.BorderThickness.Left + padded.BorderThickness.Right : 0;
                if (double.IsFinite(parent.MaxWidth)) available = Math.Min(available, Math.Max(1, parent.MaxWidth - inset - padding));
                double width = double.PositiveInfinity;
                var contentSized = IsContentSized(parent);
                if (parent is Grid grid)
                {
                    if (grid.ColumnDefinitions.Count == 0 && !contentSized) width = grid.ActualWidth;
                    else if (grid.ColumnDefinitions.Count > 0)
                    {
                        var first = Math.Min(Grid.GetColumn(child), grid.ColumnDefinitions.Count - 1);
                        var last = Math.Min(first + Grid.GetColumnSpan(child), grid.ColumnDefinitions.Count);
                        var columns = grid.ColumnDefinitions.Skip(first).Take(last - first);
                        if (columns.All(column => column.Width.IsAbsolute)) width = columns.Sum(column => column.Width.Value);
                        else if (!contentSized && columns.All(column => !column.Width.IsAuto)) width = columns.Sum(column => column.ActualWidth);
                    }
                }
                else if (parent is Border border && !contentSized) width = border.ActualWidth - padding;
                else if (parent is ScrollContentPresenter scroll && !contentSized && scroll.ViewportWidth > 0) width = scroll.ViewportWidth;
                else if (!contentSized && (parent is WrapPanel || parent is StackPanel { Orientation: Orientation.Vertical } || parent is Window))
                    width = parent.ActualWidth;
                if (double.IsFinite(width) && width > 0)
                    available = Math.Min(available, Math.Max(1, width - inset));
                inset += parent.Margin.Left + parent.Margin.Right + padding;
                child = parent;
            }
            return available;
        }

        private bool IsContentSized(FrameworkElement element)
        {
            if (element is Window window) return window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
            var current = element;
            for (var index = _ancestors.IndexOf(element) + 1; index < _ancestors.Count; index++)
            {
                if (double.IsFinite(current.Width)) return false;
                var parent = _ancestors[index];
                if (parent is Window owner) return owner.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
                if (current.HorizontalAlignment != HorizontalAlignment.Stretch || parent is WrapPanel
                    || parent is StackPanel { Orientation: Orientation.Horizontal }) return true;
                if (parent is Grid { ColumnDefinitions.Count: > 0 } grid)
                {
                    var first = Math.Min(Grid.GetColumn(current), grid.ColumnDefinitions.Count - 1);
                    var last = Math.Min(first + Grid.GetColumnSpan(current), grid.ColumnDefinitions.Count);
                    for (var column = first; column < last; column++)
                        if (grid.ColumnDefinitions[column].Width.IsAuto) return true;
                }
                current = parent;
            }
            // Popup content is measured to its desired size. Its explicit MaxWidth,
            // not yesterday's ActualWidth, is the meaningful external bound.
            return true;
        }

        private void DiscoverSelectedVisuals()
        {
            var presenter = _combo.Template?.FindName("ContentSite", _combo) as ContentPresenter
                ?? Descendants(_combo).OfType<ContentPresenter>().FirstOrDefault(candidate =>
                    Equals(candidate.Content, _combo.SelectionBoxItem));
            if (ReferenceEquals(presenter, _presenter)) return;
            ClearSelectedVisuals();
            _presenter = presenter;
            if (presenter is null) return;
            presenter.ApplyTemplate();
            IEnumerable<TextBlock> textBlocks = Descendants(presenter).OfType<TextBlock>();
            // A ComboBoxItem with visual content is represented by a VisualBrush
            // in the selection box. Observe that one selected source, not its peers.
            if (GetValue(LabelValueProperty) is UIElement selectedVisual)
                textBlocks = textBlocks.Concat(selectedVisual is TextBlock selectedText ? [selectedText] : Descendants(selectedVisual).OfType<TextBlock>());
            foreach (var text in textBlocks.Distinct())
            {
                _selectedTextBlocks.Add(text);
                TextDescriptor.AddValueChanged(text, OnSelectedTextChanged);
            }
            _measurementDirty = true;
        }

        private string ReadText()
        {
            if (GetSelectedText(_combo) is { } explicitText) return explicitText;
            if (_selectedTextBlocks.Count > 0 && (_combo.ItemTemplate is not null || _combo.ItemTemplateSelector is not null
                || GetValue(LabelValueProperty) is UIElement))
                return string.Join(" ", _selectedTextBlocks.Select(text => text.Text).Where(text => !string.IsNullOrEmpty(text)));
            return GetValue(LabelValueProperty) switch
            {
                TextBlock text => text.Text,
                ContentControl control => Convert.ToString(control.Content, CultureInfo.CurrentCulture) ?? string.Empty,
                var value => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            };
        }

        private void UpdateFullTextTip(bool clipped)
        {
            _isClipped = clipped;
            if (!clipped || string.IsNullOrEmpty(_text))
            {
                if (_fullTextTip is not null) _fullTextTip.IsOpen = false;
                _fullTextTip = null;
                RestoreMissingToolTip();
                return;
            }
            var help = _combo.ToolTip as string ?? (_combo.ToolTip as ToolTip)?.Content as string;
            var text = string.IsNullOrWhiteSpace(help) || help == _text ? _text : _text + Environment.NewLine + Environment.NewLine + help;
            if (_fullTextTip?.Content is TextBlock existing && existing.Text == text) return;
            _fullTextTip ??= new ToolTip();
            _fullTextTip.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = Math.Min(480, FindAvailableWidth()) };
            // Native WPF-UI's selected ContentPresenter is not hit-testable. Handle
            // ToolTipOpening on the ComboBox itself, without replacing its existing
            // help/binding. A missing tooltip needs a temporary trigger only.
            if (_combo.ToolTip is null)
            {
                _updatingTooltip = true;
                try { _combo.SetCurrentValue(FrameworkElement.ToolTipProperty, _emptyTip); }
                finally { _updatingTooltip = false; }
            }
        }

        private void RestoreMissingToolTip()
        {
            if (!ReferenceEquals(_combo.ToolTip, _emptyTip)) return;
            _updatingTooltip = true;
            try { _combo.SetCurrentValue(FrameworkElement.ToolTipProperty, null); }
            finally { _updatingTooltip = false; }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                // Never inspect or realize the drop-down's items.
                if (child is System.Windows.Controls.Primitives.Popup or ItemsPresenter) continue;
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        private void ClearSelectedVisuals()
        {
            foreach (var text in _selectedTextBlocks) TextDescriptor.RemoveValueChanged(text, OnSelectedTextChanged);
            _selectedTextBlocks.Clear();
            if (_fullTextTip is not null) _fullTextTip.IsOpen = false;
            _presenter = null;
            _fullTextTip = null;
            _isClipped = false;
        }

        private void Detach()
        {
            if (!_active) return;
            _active = false;
            _pending?.Abort();
            _pending = null;
            _combo.SelectionChanged -= OnSelectionChanged;
            _combo.SizeChanged -= OnSizeChanged;
            _combo.ToolTipOpening -= OnToolTipOpening;
            _combo.MouseLeave -= CloseFullTextTip;
            _combo.LostKeyboardFocus -= CloseFullTextTip;
            _combo.DropDownOpened -= CloseFullTextTip;
            _combo.PreviewKeyDown -= OnPreviewKeyDown;
            foreach (var descriptor in WatchedProperties) descriptor.RemoveValueChanged(_combo, OnControlPropertyChanged);
            foreach (var ancestor in _ancestors) ancestor.SizeChanged -= OnSizeChanged;
            _ancestors.Clear();
            if (_window is not null)
            {
                _window.DpiChanged -= OnDpiChanged;
                _window.Deactivated -= CloseFullTextTip;
            }
            _window = null;
            BindingOperations.ClearBinding(this, LabelValueProperty);
            ClearSelectedVisuals();
            RestoreMissingToolTip();
        }

        public void Dispose()
        {
            Detach();
            _combo.Loaded -= OnLoaded;
            _combo.Unloaded -= OnUnloaded;
            _combo.SetValue(DesiredMinimumWidthPropertyKey, 0d);
            _combo.SetValue(AvailableWidthPropertyKey, double.PositiveInfinity);
            _combo.SetValue(FullSelectedTextPropertyKey, string.Empty);
        }
    }
}
