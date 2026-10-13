using System.Windows;
using System.Windows.Controls;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>
/// Recycles cards of the same kind, without ever publishing a new payload to
/// the previous kind's bindings and strongly typed commands.
/// </summary>
public sealed class ProfileEditorEntryPresenter : Decorator
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry), typeof(ProfileEditorEntry), typeof(ProfileEditorEntryPresenter),
        new FrameworkPropertyMetadata(null, OnEntryChanged));

    private DataTemplate? _template;

    public ProfileEditorEntry? Entry
    {
        get => (ProfileEditorEntry?)GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    private static void OnEntryChanged(DependencyObject source, DependencyPropertyChangedEventArgs args) =>
        ((ProfileEditorEntryPresenter)source).Present((ProfileEditorEntry?)args.NewValue);

    private void Present(ProfileEditorEntry? entry)
    {
        var template = entry?.Template;
        if (template is not null && ReferenceEquals(template, _template) && Child is FrameworkElement existing)
        {
            existing.DataContext = entry!.Value;
            return;
        }

        // Each root owns its DataContext locally. The presenter's inherited
        // row context may change first during recycling, but cannot reach the
        // old template before this atomic entry transition detaches it.
        Child = null;
        _template = null;
        if (template is null) return;

        var root = template.LoadContent() as FrameworkElement
            ?? throw new InvalidOperationException("A profile editor section must create a FrameworkElement.");
        root.DataContext = entry!.Value;
        _template = template;
        Child = root;
    }
}
