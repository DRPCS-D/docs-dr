using System.Windows;
using System.Windows.Controls;
using DocsDR.App.Converter;

namespace DocsDR.App.Views;

/// <summary>Diálogos de las herramientas: buscar y reemplazar, exportar páginas como imágenes.</summary>
public static partial class Dialogs
{
    private static Grid LabeledRow(string label, Control control, double labelWidth)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        g.Children.Add(t);
        g.Children.Add(control);
        return g;
    }

    /// <summary>
    /// Ventana de buscar y reemplazar. <paramref name="apply"/> recibe (buscar, reemplazar, distinguir mayúsculas) y
    /// devuelve el mensaje de resultado; la ventana queda abierta para poder repetir la operación.
    /// </summary>
    public static void ShowReplace(string initialFind, Func<string, string, bool, string> apply)
    {
        var find = new TextBox { Text = initialFind, Width = 300 };
        var replace = new TextBox { Width = 300 };
        var matchCase = new CheckBox { Content = "Distinguir mayúsculas y minúsculas", Margin = new Thickness(0, 10, 0, 0) };
        var result = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Width = 400, MinHeight = 34 };
        var hint = new TextBlock
        {
            Text = "Se reemplaza dentro de cada renglón (no entre dos renglones). Se conserva el formato de lo que no cambia. " +
                   "Todo se puede deshacer con Ctrl+Z.",
            Opacity = 0.65, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 400, Margin = new Thickness(0, 8, 0, 0),
        };

        var all = new Button { Content = "Reemplazar todo", IsDefault = true, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Cerrar", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(all);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(LabeledRow("Buscar:", find, 100));
        panel.Children.Add(LabeledRow("Reemplazar con:", replace, 100));
        panel.Children.Add(matchCase);
        panel.Children.Add(result);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        var window = Create("Buscar y reemplazar", panel);
        all.Click += (_, _) =>
        {
            if (find.Text.Length == 0) { result.Text = "Escribe el texto a buscar."; return; }
            result.Text = apply(find.Text, replace.Text, matchCase.IsChecked == true);
        };
        window.Loaded += (_, _) => (initialFind.Length > 0 ? replace : find).Focus();
        window.ShowDialog();
    }

    public sealed record ExportImageOptions(bool Jpeg, int Dpi, List<int> Pages);

    /// <summary>Opciones para exportar páginas como imágenes. Null si se cancela.</summary>
    public static ExportImageOptions? AskExportImages(int pageCount, IReadOnlyList<int> selectedPages)
    {
        var format = new ComboBox { Width = 120, ItemsSource = new[] { "PNG", "JPEG" }, SelectedIndex = 0 };
        var dpi = new ComboBox { Width = 120, ItemsSource = new[] { 72, 150, 200, 300 }, SelectedItem = 150 };

        var allRadio = new RadioButton { Content = $"Todas las páginas ({pageCount})", IsChecked = true, GroupName = "p", Margin = new Thickness(0, 12, 0, 0) };
        var selRadio = new RadioButton
        {
            Content = $"Las páginas elegidas en las miniaturas ({selectedPages.Count})", GroupName = "p",
            IsEnabled = selectedPages.Count > 0, Margin = new Thickness(0, 6, 0, 0),
        };
        var rangeRadio = new RadioButton { Content = "Rango:", GroupName = "p", VerticalAlignment = VerticalAlignment.Center };
        var rangeBox = new TextBox { Text = "1-" + pageCount, Width = 200, Margin = new Thickness(8, 0, 0, 0) };
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        rangeRow.Children.Add(rangeRadio);
        rangeRow.Children.Add(rangeBox);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(LabeledRow("Formato:", format, 120));
        panel.Children.Add(LabeledRow("Resolución (ppp):", dpi, 120));
        panel.Children.Add(allRadio);
        panel.Children.Add(selRadio);
        panel.Children.Add(rangeRow);
        panel.Children.Add(error);
        panel.Children.Add(Buttons(out var ok, "Exportar…"));

        ExportImageOptions? result = null;
        var window = Create("Exportar páginas como imágenes", panel);
        ok.Click += (_, _) =>
        {
            List<int>? pages = allRadio.IsChecked == true ? Enumerable.Range(0, pageCount).ToList()
                : selRadio.IsChecked == true ? selectedPages.Order().ToList()
                : ConverterViewModel.ParsePageRange(rangeBox.Text, pageCount);
            if (pages is null || pages.Count == 0 || pages.Any(p => p < 0 || p >= pageCount))
            {
                error.Text = "Rango no válido. Ejemplos: 2-5, 1, 3, 7";
                return;
            }
            result = new ExportImageOptions(format.SelectedIndex == 1, (int)dpi.SelectedItem, pages);
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? result : null;
    }
}
