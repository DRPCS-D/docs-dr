using System.Windows;
using System.Windows.Controls;
using DocsDR.App.Converter;
using Microsoft.Win32;

namespace DocsDR.App.Views;

/// <summary>Diálogos sencillos construidos en código, para no multiplicar archivos XAML.</summary>
public static partial class Dialogs
{
    private static Window Create(string title, UIElement content) => new()
    {
        Title = title,
        Content = content,
        SizeToContent = SizeToContent.WidthAndHeight,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = Application.Current.MainWindow,
    };

    private static StackPanel Buttons(out Button ok, string okText = "Aceptar")
    {
        ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 80 };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        return panel;
    }

    /// <summary>Pide un texto (varias líneas). Devuelve null si se cancela.</summary>
    public static string? AskText(string title, string prompt, string initial = "")
    {
        var box = new TextBox
        {
            Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 380, Height = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = "Ctrl+Enter para aceptar", Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(Buttons(out var ok));

        var window = Create(title, panel);
        ok.Click += (_, _) => window.DialogResult = true;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                window.DialogResult = true;
        };
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>
    /// Pregunta cómo dividir el documento. Devuelve los rangos (índices base 0) de cada archivo resultante,
    /// o null si se cancela.
    /// </summary>
    public static List<List<int>>? AskSplit(int pageCount)
    {
        var everyRadio = new RadioButton { Content = "Cada", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, GroupName = "m" };
        var everyBox = new TextBox { Text = "1", Width = 50, Margin = new Thickness(6, 0, 6, 0), TextAlignment = TextAlignment.Center };
        var every = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        every.Children.Add(everyRadio);
        every.Children.Add(everyBox);
        every.Children.Add(new TextBlock { Text = "página(s)", VerticalAlignment = VerticalAlignment.Center });

        var rangesRadio = new RadioButton { Content = "Por rangos (un archivo por rango):", Margin = new Thickness(0, 10, 0, 0), GroupName = "m" };
        var rangesBox = new TextBox { Text = pageCount > 1 ? $"1-{pageCount / 2}, {pageCount / 2 + 1}-{pageCount}" : "1", Width = 300, Margin = new Thickness(0, 4, 0, 0) };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"El documento tiene {pageCount} página(s). Dividir:" });
        panel.Children.Add(every);
        panel.Children.Add(rangesRadio);
        panel.Children.Add(rangesBox);
        panel.Children.Add(error);
        panel.Children.Add(Buttons(out var ok, "Dividir"));

        List<List<int>>? result = null;
        var window = Create("Dividir documento", panel);
        ok.Click += (_, _) =>
        {
            if (everyRadio.IsChecked == true)
            {
                if (!int.TryParse(everyBox.Text, out int n) || n < 1) { error.Text = "Escribe un número válido."; return; }
                result = Enumerable.Range(0, (pageCount + n - 1) / n)
                    .Select(i => Enumerable.Range(i * n, Math.Min(n, pageCount - i * n)).ToList()).ToList();
            }
            else
            {
                var groups = rangesBox.Text.Split(',', ';').Select(g => ConverterViewModel.ParsePageRange(g, pageCount)).ToList();
                if (groups.Count == 0 || groups.Any(g => g is null || g.Count == 0)) { error.Text = "Rangos no válidos. Ejemplo: 1-3, 4-6, 7"; return; }
                result = groups.Select(g => g!).ToList();
            }
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>Lista de PDFs a unir, con orden editable. Devuelve las rutas en orden o null si se cancela.</summary>
    public static List<string>? AskMerge() =>
        AskFiles("Unir PDFs", "Agrega los PDF y ordénalos; se unirán en ese orden.", "Documentos PDF (*.pdf)|*.pdf", "Unir…", minCount: 2);

    /// <summary>Lista de archivos con orden editable (subir/bajar/quitar). Null si se cancela.</summary>
    public static List<string>? AskFiles(string title, string prompt, string filter, string okText, int minCount)
    {
        var list = new ListBox { Width = 460, Height = 200, SelectionMode = SelectionMode.Single };
        Button Btn(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 6), MinWidth = 90 };
        var add = Btn("Añadir…"); var up = Btn("Subir"); var down = Btn("Bajar"); var remove = Btn("Quitar");
        var side = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        foreach (var b in new[] { add, up, down, remove }) side.Children.Add(b);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(list);
        row.Children.Add(side);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(row);
        panel.Children.Add(Buttons(out var ok, okText));

        add.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = filter, Multiselect = true, Title = "Agregar archivos" };
            if (dlg.ShowDialog() == true) foreach (var f in dlg.FileNames) list.Items.Add(f);
        };
        remove.Click += (_, _) => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var item = list.Items[i];
            list.Items.RemoveAt(i);
            list.Items.Insert(j, item);
            list.SelectedIndex = j;
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        var window = Create(title, panel);
        ok.Click += (_, _) =>
        {
            if (list.Items.Count < minCount)
            {
                Msg.Show(window, minCount == 1 ? "Agrega al menos un archivo." : $"Agrega al menos {minCount} archivos.", title);
                return;
            }
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? list.Items.Cast<string>().ToList() : null;
    }
}
