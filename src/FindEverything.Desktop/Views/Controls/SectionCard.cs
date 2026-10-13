using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>
/// A native WPF-UI card whose stretching header is also a logical child.
/// WPF-UI's Header property does not attach its value to the logical tree, so
/// editors in collapsed cards would otherwise lose inherited bindings and
/// resources until the visual template is created.
/// </summary>
public sealed class SectionCard : CardControl
{
    static SectionCard()
    {
        HeaderProperty.OverrideMetadata(
            typeof(SectionCard),
            new FrameworkPropertyMetadata(null, OnHeaderChanged));
    }

    protected override IEnumerator LogicalChildren => EnumerateLogicalChildren().GetEnumerator();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // WPF-UI 4.3.0 centers HeaderContentPresenter regardless of the card's
        // VerticalContentAlignment. Keep its native template and theme states,
        // but honor our Stretch/Top setting for full-height editor surfaces.
        if (GetTemplateChild("HeaderContentPresenter") is ContentPresenter headerPresenter)
        {
            headerPresenter.SetBinding(VerticalAlignmentProperty,
                new Binding(nameof(VerticalContentAlignment)) { Source = this });
        }
    }

    private static void OnHeaderChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var card = (SectionCard)source;
        card.RemoveLogicalChild(args.OldValue);
        card.AddLogicalChild(args.NewValue);
    }

    private IEnumerable<object> EnumerateLogicalChildren()
    {
        if (Header is { } header)
        {
            yield return header;
        }

        var contentChildren = base.LogicalChildren;
        if (contentChildren is null)
        {
            yield break;
        }

        while (contentChildren.MoveNext())
        {
            if (contentChildren.Current is { } child && !ReferenceEquals(child, Header))
            {
                yield return child;
            }
        }
    }
}
