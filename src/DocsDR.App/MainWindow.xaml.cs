using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DocsDR.App.ViewModels;

namespace DocsDR.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        InputBindings.Add(new KeyBinding(new FocusSearchCommand(this), Key.F, ModifierKeys.Control));
        RestoreWindow();
    }

    /// <summary>Devuelve la ventana a donde y como estaba al cerrar (si sigue cabiendo en alguna pantalla).</summary>
    private void RestoreWindow()
    {
        if (_vm.WindowPlacement is not { } w) return;
        bool fits = w.Width >= 600 && w.Height >= 400
            && w.Left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && w.Left + w.Width - 100 > SystemParameters.VirtualScreenLeft
            && w.Top >= SystemParameters.VirtualScreenTop - 10
            && w.Top + 100 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!fits) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = w.Left; Top = w.Top; Width = w.Width; Height = w.Height;
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Al cambiar de pestaña de la cinta se suelta la herramienta activa, para no dibujar o editar sin querer.</summary>
    private void OnRibbonSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Los cuadros combinados y demás controles de dentro también elevan este evento; solo importa la cinta.
        if (!ReferenceEquals(e.OriginalSource, Ribbon)) return;
        if (_vm.SelectedDocument is { } doc) doc.Tool = AnnotTool.Select;
    }

    /// <summary>Antes de cerrar la app se ofrece guardar los documentos con cambios.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Se guarda antes de cerrar los documentos: es lo que se reabrirá la próxima vez.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _vm.SaveView(new Services.WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized), null);
        if (!_vm.ConfirmCloseAll()) e.Cancel = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            _vm.OpenPath(f);
    }

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm.SelectedDocument is null) return;
        if (int.TryParse(PageBox.Text, out int page)) _vm.SelectedDocument.GoToPage(page - 1);
        else PageBox.Text = _vm.SelectedDocument.CurrentPage.ToString();
    }

    private sealed class FocusSearchCommand(MainWindow window) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            window.SearchBox.Focus();
            window.SearchBox.SelectAll();
        }
    }
}
