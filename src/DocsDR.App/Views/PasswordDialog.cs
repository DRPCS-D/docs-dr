using System.Windows;
using System.Windows.Controls;

namespace DocsDR.App.Views;

/// <summary>Diálogo mínimo para pedir la contraseña de un PDF protegido.</summary>
public static class PasswordDialog
{
    public static string? Ask(string fileName)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 260 };
        var ok = new Button { Content = "Abrir", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"«{fileName}» está protegido con contraseña.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Contraseña requerida",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }
}
