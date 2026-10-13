using System.Windows;
using System.Windows.Controls;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Views.Controls;

public sealed class TextFileEditorDialog : Ui.ContentDialog
{
    private readonly Ui.TextBox _editor;
    private readonly Ui.TextBlock _error;
    private readonly Func<string, Task<bool>> _save;
    private bool _saving;

    public TextFileEditorDialog(TextFileEditDocument document, IAppLocalizer localizer, Func<string, Task<bool>> save)
    {
        _save = save;
        // Implicit style lookup uses the concrete subclass type, not its base.
        // Keep the native Fluent ContentDialog template for this specialized editor.
        SetResourceReference(StyleProperty, typeof(Ui.ContentDialog));
        Title = localizer.Get(document.IsCreation ? "Loc.FileEditor.Create.Title" : "Loc.FileEditor.Edit.Title",
            document.IsCreation ? "텍스트 파일 생성" : "텍스트 파일 수정");
        PrimaryButtonText = localizer.Get("Loc.Common.Save", "저장"); CloseButtonText = localizer.Get("Loc.Common.Cancel", "취소");
        IsSecondaryButtonEnabled = false;
        DefaultButton = Ui.ContentDialogButton.Close;
        var host = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current?.MainWindow;
        var width = Math.Clamp((host?.ActualWidth > 0 ? host.ActualWidth : 840) - 128, 280, 640);
        var contentHeight = Math.Clamp((host?.ActualHeight > 0 ? host.ActualHeight : 720) - 340, 280, 600);
        DialogMaxWidth = width + 64; DialogMaxHeight = Math.Max(320, (host?.ActualHeight > 0 ? host.ActualHeight : 720) - 48);
        DialogMargin = new Thickness(24);
        // A StackPanel participates in logical scrolling. Focusing the editor
        // then scrolls an entire preceding child out of view. Keep the path and
        // errors in fixed rows and let only the actual text editor consume space.
        ScrollViewer.SetCanContentScroll(this, false);
        var panel = new Grid { MinWidth = 280, MaxWidth = width, Height = contentHeight };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var path = new Ui.TextBlock { Name = "FileTargetPath", Text = document.FilePath, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
            Margin = new(0, 0, 0, 8), MaxHeight = 60, ToolTip = document.FilePath };
        panel.Children.Add(path);
        var help = new Ui.TextBlock { Text = localizer.Get("Loc.FileEditor.Help", "저장을 눌러야 실제 파일에 반영됩니다. 외부에서 변경된 파일은 덮어쓰지 않습니다."),
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        Grid.SetRow(help, 1); panel.Children.Add(help);
        _editor = new Ui.TextBox { Text = document.Content, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            MinHeight = 80, MaxLength = (int)Math.Min(int.MaxValue, document.Field.MaxBytes),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ClearButtonEnabled = false };
        Grid.SetRow(_editor, 2); panel.Children.Add(_editor);
        _error = new Ui.TextBlock { Name = "FileEditError", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), MaxHeight = 64, Visibility = Visibility.Collapsed };
        _error.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetRow(_error, 3); panel.Children.Add(_error);
        Content = panel;
    }

    protected override async void OnButtonClick(Ui.ContentDialogButton button)
    {
        if (_saving) return;
        if (button != Ui.ContentDialogButton.Primary) { base.OnButtonClick(button); return; }
        _saving = true;
        // WPF-UI uses the *property* as footer visibility. Disable the actual
        // button instead, so the two-button footer does not resize while saving.
        var primary = Template?.FindName("PrimaryButton", this) as Ui.Button;
        var close = Template?.FindName("CloseButton", this) as Ui.Button;
        primary?.SetCurrentValue(IsEnabledProperty, false);
        close?.SetCurrentValue(IsEnabledProperty, false);
        _editor.IsReadOnly = true;
        try { if (await _save(_editor.Text)) Hide(Ui.ContentDialogResult.Primary); }
        catch (Exception exception) { _error.Text = exception.Message; _error.Visibility = Visibility.Visible; }
        finally
        {
            _saving = false;
            primary?.SetCurrentValue(IsEnabledProperty, true);
            close?.SetCurrentValue(IsEnabledProperty, true);
            _editor.IsReadOnly = false;
        }
    }
}
