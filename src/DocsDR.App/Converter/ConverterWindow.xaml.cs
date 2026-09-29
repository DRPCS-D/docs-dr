using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace DocsDR.App.Converter;

public partial class ConverterWindow : Window
{
    private readonly ConverterViewModel _vm;

    public ConverterWindow(ConverterViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Overlay.ViewModel = vm;
        vm.OverlayInvalidated += Overlay.InvalidateVisual;
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            vm.CancelCommand.Execute(null);
            vm.OverlayInvalidated -= Overlay.InvalidateVisual;
            vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConverterViewModel.SelectedTable) or nameof(ConverterViewModel.Mode))
            Overlay.InvalidateVisual();
    }

    /// <summary>Las columnas del DataTable se llaman c0, c1…; el encabezado visible es el Caption.</summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (_vm.Preview?.Table?.Columns[e.PropertyName] is DataColumn col)
            e.Column.Header = col.Caption;
    }
}
