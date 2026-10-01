using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DocsDR.App;

/// <summary>
/// Cuadro de mensaje con el tema de la aplicación (claro/oscuro). Sustituye a <see cref="MessageBox"/>,
/// con la misma forma de uso: texto, título, botones e icono.
/// </summary>
public static class Msg
{
    public static MessageBoxResult Show(string text, string caption = "DOCS-DR",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        Show(ActiveWindow(), text, caption, buttons, image);

    public static MessageBoxResult Show(Window? owner, string text, string caption = "DOCS-DR",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var (glyph, color, sound) = image switch
        {
            MessageBoxImage.Question => ("", Color.FromRgb(0x3B, 0x82, 0xF6), SystemSounds.Question),
            MessageBoxImage.Warning => ("", Color.FromRgb(0xF5, 0xA6, 0x23), SystemSounds.Exclamation),
            MessageBoxImage.Error => ("", Color.FromRgb(0xE5, 0x48, 0x4D), SystemSounds.Hand),
            MessageBoxImage.Information => ("", Color.FromRgb(0x3B, 0x82, 0xF6), SystemSounds.Asterisk),
            _ => ("", Colors.Transparent, SystemSounds.Beep),
        };

        var result = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };
        var window = new Window
        {
            Title = caption,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 340,
            MaxWidth = 520,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
        };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        void Add(string label, MessageBoxResult value, bool isDefault, bool isCancel)
        {
            var b = new Button { Content = new AccessText { Text = label }, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            if (isDefault) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            b.Click += (_, _) => { result = value; window.Close(); };
            buttonRow.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK:
                Add("_Aceptar", MessageBoxResult.OK, true, true); break;
            case MessageBoxButton.OKCancel:
                Add("_Aceptar", MessageBoxResult.OK, true, false); Add("_Cancelar", MessageBoxResult.Cancel, false, true); break;
            case MessageBoxButton.YesNo:
                Add("_Sí", MessageBoxResult.Yes, true, false); Add("_No", MessageBoxResult.No, false, true); break;
            default:
                Add("_Sí", MessageBoxResult.Yes, true, false); Add("_No", MessageBoxResult.No, false, false); Add("_Cancelar", MessageBoxResult.Cancel, false, true); break;
        }

        var body = new Grid { Margin = new Thickness(24, 22, 24, 18) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var textColumn = 0;
        if (glyph.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30,
                Foreground = new SolidColorBrush(color), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
            });
            textColumn = 1;
        }
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, MinHeight = 30 });
        content.Children.Add(buttonRow);
        Grid.SetColumn(content, textColumn);
        if (textColumn == 0) Grid.SetColumnSpan(content, 2);
        body.Children.Add(content);
        window.Content = body;

        // Ctrl+C copia el mensaje, como en el cuadro de Windows
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { try { Clipboard.SetText($"{caption}\n\n{text}"); } catch { } }
        };
        window.Loaded += (_, _) => sound.Play();
        window.ShowDialog();
        return result;
    }

    private static Window? ActiveWindow()
    {
        var app = Application.Current;
        if (app is null) return null;
        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        if (active is not null) return active;
        return app.MainWindow is { IsVisible: true } main ? main : null;
    }
}
