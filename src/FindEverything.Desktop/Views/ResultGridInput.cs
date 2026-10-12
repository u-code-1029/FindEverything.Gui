using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace FindEverything.Desktop.Views;

internal static class ResultGridInput
{
    internal static bool IsInteractiveOrigin(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBoxBase or PasswordBox or ButtonBase or ComboBox or Slider
                or ScrollBar or Thumb or Hyperlink) return true;
            if (source is DataGridRow) return false;
            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(source),
            };
        }

        return false;
    }

    internal static bool IsClickGesture(Point start, Point end, int clickCount) =>
        clickCount == 1
        && Math.Abs(end.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
        && Math.Abs(end.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance;
}
