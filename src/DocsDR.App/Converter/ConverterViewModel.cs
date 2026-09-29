using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocsDR.App.Services;
using DocsDR.Core;
using DocsDR.Excel;
using DocsDR.TableExtraction;
using Microsoft.Win32;

namespace DocsDR.App.Converter;

public enum OverlayMode { Select, DrawTable, AddColumn, AddRow }

public sealed partial class ConverterPageItem(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Label => $"Página {Index + 1}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _tableCount;

    public string Summary => TableCount == 0 ? "" : TableCount == 1 ? "1 tabla" : $"{TableCount} tablas";
}

public sealed record ColumnTypeOption(ColumnType Value, string Name);

public sealed partial class ColumnTypeItem(int index, string header, ColumnType type, Action<ColumnTypeItem> changed) : ObservableObject
{
    public int Index { get; } = index;
    public string Header { get; } = header;

    [ObservableProperty] private ColumnType _type = type;

    partial void OnTypeChanged(ColumnType value) => changed(this);
}

/// <summary>
/// Módulo PDF → Excel. Flujo: 1 Analizar (detección automática) → 2 Revisar/definir tablas (overlay)
/// → 3 Previsualizar datos limpios (editable) → 4 Exportar.
/// </summary>
public sealed partial class ConverterViewModel : ObservableObject
{
    public const double PageZoom = 1.4;

    public static IReadOnlyList<ColumnTypeOption> ColumnTypeOptions { get; } =
    [
        new(ColumnType.Text, "Texto"),
        new(ColumnType.Number, "Número"),
        new(ColumnType.Currency, "Moneda"),
        new(ColumnType.Percent, "Porcentaje"),
        new(ColumnType.Date, "Fecha"),
    ];

    public static IReadOnlyList<string> DecimalModes { get; } = ["Automático", "Coma (1.234,56)", "Punto (1,234.56)"];
    public static IReadOnlyList<string> ContinuationModes { get; } = ["Automático", "Unir con la página anterior", "No unir"];

    private readonly IPdfDocument _doc;
    private readonly List<TableDefinition> _defs = [];
    private readonly Dictionary<string, Dictionary<int, ColumnType>> _typeOverrides = [];
    private CancellationTokenSource? _cts;
    private int _buildVersion;

    public ConverterViewModel(IPdfDocument doc)
    {
        _doc = doc;
        PageItems = new(Enumerable.Range(0, doc.PageCount).Select(i => new ConverterPageItem(i)));
        SelectedPage = PageItems.FirstOrDefault();
    }

    public string Title => $"Convertir a Excel — {Path.GetFileName(_doc.FilePath)}";
    public ObservableCollection<ConverterPageItem> PageItems { get; }
    public ObservableCollection<CleanTable> Results { get; } = [];
    public ObservableCollection<ColumnTypeItem> ColumnTypes { get; } = [];

    /// <summary>La vista redibuja el overlay cuando cambian las definiciones.</summary>
    public event Action? OverlayInvalidated;

    public IEnumerable<TableDefinition> CurrentTables => _defs.Where(d => d.PageIndex == SelectedPage?.Index);

    [ObservableProperty] private ConverterPageItem? _selectedPage;
    [ObservableProperty] private ImageSource? _pageImage;
    [ObservableProperty] private double _pageDisplayWidth;
    [ObservableProperty] private double _pageDisplayHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTable), nameof(SelectedHeaderRows), nameof(SelectedContinuationIndex),
        nameof(SelectedManualRows), nameof(SelectedInfo))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(ApplyToPagesCommand))]
    private TableDefinition? _selectedTable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectMode), nameof(IsDrawMode), nameof(IsAddColumnMode), nameof(IsAddRowMode), nameof(ModeHint))]
    private OverlayMode _mode = OverlayMode.Select;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectAllCommand), nameof(DetectPageCommand), nameof(ExportExcelCommand), nameof(ExportCsvCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Pulsa «Detectar tablas» o dibuja una tabla sobre la página.";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private double _progressMax = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    private int _currentStep = 1;

    [ObservableProperty] private string _applyPagesText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedResultInfo))]
    private CleanTable? _selectedResult;

    [ObservableProperty] private DataView? _preview;

    // Opciones de limpieza y exportación
    [ObservableProperty] private bool _consolidated;
    [ObservableProperty] private bool _includeSourceSheet = true;
    [ObservableProperty] private bool _mergeWrappedRows = true;
    [ObservableProperty] private bool _removeRepeatedHeaders = true;
    [ObservableProperty] private int _decimalModeIndex;

    partial void OnMergeWrappedRowsChanged(bool value) => _ = RebuildAsync();
    partial void OnRemoveRepeatedHeadersChanged(bool value) => _ = RebuildAsync();
    partial void OnDecimalModeIndexChanged(int value) => _ = RebuildAsync();

    public string StepText => CurrentStep switch
    {
        1 => "Paso 1 de 4 · Analizar el documento",
        2 => "Paso 2 de 4 · Revisar y ajustar las tablas",
        3 => "Paso 3 de 4 · Previsualizar los datos",
        _ => "Paso 4 de 4 · Exportado",
    };

    public bool IsSelectMode { get => Mode == OverlayMode.Select; set { if (value) Mode = OverlayMode.Select; } }
    public bool IsDrawMode { get => Mode == OverlayMode.DrawTable; set { if (value) Mode = OverlayMode.DrawTable; } }
    public bool IsAddColumnMode { get => Mode == OverlayMode.AddColumn; set { if (value) Mode = OverlayMode.AddColumn; } }
    public bool IsAddRowMode { get => Mode == OverlayMode.AddRow; set { if (value) Mode = OverlayMode.AddRow; } }

    public string ModeHint => Mode switch
    {
        OverlayMode.DrawTable => "Arrastra un rectángulo alrededor de la tabla. Las columnas se sugieren automáticamente.",
        OverlayMode.AddColumn => "Haz clic dentro de una tabla para agregar un separador de columna.",
        OverlayMode.AddRow => "Haz clic dentro de una tabla para agregar un separador de fila.",
        _ => "Clic: seleccionar · Arrastrar bordes/separadores: ajustar · Clic derecho en separador: quitar · Supr: eliminar tabla",
    };

    public bool HasSelectedTable => SelectedTable is not null;

    public string SelectedInfo => SelectedTable is not { } t ? "Ninguna tabla seleccionada"
        : $"{MethodName(t.Method)} · {t.ColumnCount} columnas" + (t.Method == DetectionMethod.Manual ? "" : $" · confianza {t.Confidence:P0}");

    public string SelectedResultInfo => SelectedResult is not { } r ? ""
        : $"{r.Rows.Count} filas · {r.Headers.Count} columnas · páginas {string.Join(", ", r.SourcePages)}";

    private static string MethodName(DetectionMethod m) => m switch
    {
        DetectionMethod.Lattice => "Con bordes",
        DetectionMethod.Stream => "Sin bordes",
        _ => "Manual",
    };

    public int SelectedHeaderRows
    {
        get => SelectedTable?.HeaderRows ?? 1;
        set
        {
            if (SelectedTable is null) return;
            SelectedTable.HeaderRows = Math.Clamp(value, 0, 10);
            OnPropertyChanged();
            DefinitionsChanged();
        }
    }

    public int SelectedContinuationIndex
    {
        get => SelectedTable?.ContinuesPrevious switch { true => 1, false => 2, _ => 0 };
        set
        {
            if (SelectedTable is null) return;
            SelectedTable.ContinuesPrevious = value switch { 1 => true, 2 => false, _ => null };
            OnPropertyChanged();
            DefinitionsChanged();
        }
    }

    /// <summary>Filas manuales: convierte las filas automáticas en separadores editables.</summary>
    public bool SelectedManualRows
    {
        get => SelectedTable?.RowSeparators.Count > 0;
        set
        {
            if (SelectedTable is null) return;
            SelectedTable.RowSeparators = value
                ? TableDetector.AutoRowSeparators(SelectedTable, _doc.GetPageContent(SelectedTable.PageIndex).Words)
                : [];
            OnPropertyChanged();
            DefinitionsChanged();
        }
    }

    partial void OnSelectedPageChanged(ConverterPageItem? value)
    {
        SelectedTable = CurrentTables.FirstOrDefault();
        _ = LoadPageImageAsync(value);
        OverlayInvalidated?.Invoke();
    }

    private async Task LoadPageImageAsync(ConverterPageItem? page)
    {
        if (page is null) { PageImage = null; return; }
        var (w, h) = _doc.GetPageSize(page.Index);
        PageDisplayWidth = w * PageZoom;
        PageDisplayHeight = h * PageZoom;
        var bmp = await PageImages.RenderAsync(_doc, page.Index, PageZoom);
        if (SelectedPage == page) PageImage = bmp;
    }

    // ---------------- Detección ----------------

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DetectAllAsync()
    {
        if (_defs.Count > 0 && MessageBox.Show("Se reemplazarán todas las tablas definidas. ¿Continuar?", "Detectar tablas",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        ProgressMax = _doc.PageCount;
        Progress = 0;
        Status = "Analizando páginas…";
        try
        {
            var progress = new Progress<int>(p => { Progress = p; Status = $"Analizando página {p} de {_doc.PageCount}…"; });
            var ct = _cts.Token;
            var found = await Task.Run(() => ConversionPipeline.DetectAll(_doc, progress, ct), ct);
            _defs.Clear();
            _defs.AddRange(found);
            SelectedTable = CurrentTables.FirstOrDefault();
            DefinitionsChanged();
            CurrentStep = 2;

            int noOcr = await Task.Run(() => Enumerable.Range(0, _doc.PageCount).Count(i => _doc.GetPageContent(i).OcrUnavailable));
            Status = found.Count == 0
                ? "No se detectaron tablas. Usa «Dibujar tabla» para definirlas manualmente."
                : $"Se detectaron {found.Count} tabla(s). Revisa cada página y ajusta si es necesario.";
            if (noOcr > 0)
                Status += $"  ⚠ {noOcr} página(s) escaneada(s) sin OCR: instala los datos de idioma (ver README, scripts/get-tessdata.ps1).";
        }
        catch (OperationCanceledException)
        {
            Status = "Detección cancelada.";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DetectPageAsync()
    {
        if (SelectedPage is null) return;
        int idx = SelectedPage.Index;
        IsBusy = true;
        Status = $"Analizando página {idx + 1}…";
        try
        {
            var found = await Task.Run(() => TableDetector.DetectPage(_doc.GetPageContent(idx)));
            _defs.RemoveAll(d => d.PageIndex == idx);
            _defs.AddRange(found);
            SelectedTable = CurrentTables.FirstOrDefault();
            DefinitionsChanged();
            if (CurrentStep < 2) CurrentStep = 2;
            Status = found.Count == 0 ? "No se detectaron tablas en esta página." : $"{found.Count} tabla(s) en la página {idx + 1}.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ClearPage()
    {
        if (SelectedPage is null) return;
        _defs.RemoveAll(d => d.PageIndex == SelectedPage.Index);
        SelectedTable = null;
        DefinitionsChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTable))]
    private void DeleteSelected()
    {
        if (SelectedTable is null) return;
        _defs.Remove(SelectedTable);
        SelectedTable = CurrentTables.FirstOrDefault();
        DefinitionsChanged();
    }

    /// <summary>Copia la tabla seleccionada a otras páginas ("2-5, 8" o "todas"), reemplazando las existentes.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedTable))]
    private void ApplyToPages()
    {
        if (SelectedTable is null) return;
        var pages = ParsePageRange(ApplyPagesText, _doc.PageCount);
        if (pages is null || pages.Count == 0)
        {
            Status = "Rango de páginas no válido. Ejemplos: «2-5», «2, 4, 6», «todas».";
            return;
        }
        var copies = TableDetector.ApplyToPages(SelectedTable, pages).ToList();
        _defs.RemoveAll(d => d.PageIndex != SelectedTable.PageIndex && pages.Contains(d.PageIndex));
        _defs.AddRange(copies);
        DefinitionsChanged();
        Status = $"Definición aplicada a {copies.Count} página(s).";
    }

    internal static List<int>? ParsePageRange(string text, int pageCount)
    {
        text = text.Trim();
        if (text.Equals("todas", StringComparison.OrdinalIgnoreCase) || text == "*")
            return Enumerable.Range(0, pageCount).ToList();
        var result = new SortedSet<int>();
        foreach (var part in text.Split(',', ';').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length == 1 && int.TryParse(ends[0], out int single)) result.Add(single - 1);
            else if (ends.Length == 2 && int.TryParse(ends[0], out int a) && int.TryParse(ends[1], out int b))
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) result.Add(i - 1);
            else return null;
        }
        return result.Where(i => i >= 0 && i < pageCount).ToList();
    }

    // ---------------- Edición manual (llamado desde el overlay) ----------------

    public void SelectTable(TableDefinition? def) => SelectedTable = def;

    public void OnRegionDrawn(PdfRect region)
    {
        if (SelectedPage is null) return;
        var def = TableDetector.FromUserRegion(_doc.GetPageContent(SelectedPage.Index), region);
        _defs.Add(def);
        SelectedTable = def;
        Mode = OverlayMode.Select;
        if (CurrentStep < 2) CurrentStep = 2;
        DefinitionsChanged();
        Status = $"Tabla definida con {def.ColumnCount} columna(s). Ajusta los separadores si hace falta.";
    }

    public void AddColumnAt(TableDefinition def, double x)
    {
        def.ColumnSeparators.Add(x);
        SelectedTable = def;
        NotifyEdited(def);
    }

    public void AddRowAt(TableDefinition def, double y)
    {
        if (def.RowSeparators.Count == 0)
            def.RowSeparators = TableDetector.AutoRowSeparators(def, _doc.GetPageContent(def.PageIndex).Words);
        def.RowSeparators.Add(y);
        SelectedTable = def;
        NotifyEdited(def);
        OnPropertyChanged(nameof(SelectedManualRows));
    }

    public void RemoveSeparator(TableDefinition def, bool column, int index)
    {
        var list = column ? def.ColumnSeparators : def.RowSeparators;
        if (index >= 0 && index < list.Count) list.RemoveAt(index);
        NotifyEdited(def);
        OnPropertyChanged(nameof(SelectedManualRows));
    }

    /// <summary>Normaliza una definición tras arrastrar bordes o separadores.</summary>
    public void NotifyEdited(TableDefinition def)
    {
        var r = def.Region;
        def.ColumnSeparators = def.ColumnSeparators.Where(x => x > r.X0 + 1 && x < r.X1 - 1).Distinct().Order().ToList();
        def.RowSeparators = def.RowSeparators.Where(y => y > r.Y0 + 1 && y < r.Y1 - 1).Distinct().Order().ToList();
        OnPropertyChanged(nameof(SelectedInfo));
        DefinitionsChanged();
    }

    private void DefinitionsChanged()
    {
        foreach (var p in PageItems) p.TableCount = _defs.Count(d => d.PageIndex == p.Index);
        OverlayInvalidated?.Invoke();
        _ = RebuildAsync();
    }

    // ---------------- Resultado ----------------

    private CleanOptions OptionsFor(string tableName) => new()
    {
        MergeWrappedRows = MergeWrappedRows,
        RemoveRepeatedHeaders = RemoveRepeatedHeaders,
        DecimalSeparator = (DecimalSeparator)DecimalModeIndex,
        ColumnTypeOverrides = _typeOverrides.TryGetValue(tableName, out var o) ? new(o) : [],
    };

    private async Task RebuildAsync()
    {
        int version = ++_buildVersion;
        var defs = _defs.Select(d => d.Clone()).ToList();
        List<CleanTable> tables;
        try
        {
            tables = await Task.Run(() =>
            {
                var grids = defs.Select(d => GridBuilder.Build(d, _doc.GetPageContent(d.PageIndex).Words))
                                .Where(g => g.Rows.Count > 0);
                return MultiPageMerger.Merge(grids).Select(t => DataCleaner.Clean(t, OptionsFor(t.Name))).ToList();
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error al construir las tablas");
            Status = "Error al construir las tablas: " + ex.Message;
            return;
        }
        if (version != _buildVersion) return; // llegó un cambio más reciente

        var previousName = SelectedResult?.Name;
        Results.Clear();
        foreach (var t in tables) Results.Add(t);
        SelectedResult = Results.FirstOrDefault(r => r.Name == previousName) ?? Results.FirstOrDefault();
        if (Results.Count > 0 && CurrentStep < 3) CurrentStep = 3;
        ExportExcelCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedResultChanged(CleanTable? value)
    {
        ColumnTypes.Clear();
        if (value is null) { Preview = null; return; }

        for (int c = 0; c < value.Headers.Count; c++)
            ColumnTypes.Add(new ColumnTypeItem(c, value.Headers[c], value.Types[c], OnColumnTypeChanged));

        var table = new DataTable();
        for (int c = 0; c < value.Headers.Count; c++)
            table.Columns.Add(new DataColumn($"c{c}", typeof(string)) { Caption = value.Headers[c] });
        foreach (var row in value.Rows)
            table.Rows.Add(row.Select((v, c) => (object)Display(v, value.Types[c])).ToArray());
        table.AcceptChanges();
        table.ColumnChanged += (_, e) => OnPreviewEdited(value, e);
        Preview = table.DefaultView;
    }

    private void OnColumnTypeChanged(ColumnTypeItem item)
    {
        if (SelectedResult is null) return;
        if (!_typeOverrides.TryGetValue(SelectedResult.Name, out var map))
            _typeOverrides[SelectedResult.Name] = map = [];
        map[item.Index] = item.Type;
        _ = RebuildAsync();
    }

    /// <summary>Aplica la edición manual de una celda de la vista previa a la tabla que se exportará.</summary>
    private static void OnPreviewEdited(CleanTable target, DataColumnChangeEventArgs e)
    {
        int row = e.Row.Table.Rows.IndexOf(e.Row);
        int col = e.Column!.Ordinal;
        if (row < 0 || row >= target.Rows.Count) return;
        target.Rows[row][col] = DataCleaner.Parse(e.ProposedValue as string ?? "", target.Types[col], DecimalSeparator.Auto);
    }

    private static string Display(CellValue v, ColumnType type) => v.Value switch
    {
        null => "",
        decimal d when type == ColumnType.Percent => d.ToString("P2", CultureInfo.CurrentCulture),
        decimal d => d.ToString("#,##0.##", CultureInfo.CurrentCulture),
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture),
        _ => v.Raw,
    };

    // ---------------- Exportación ----------------

    private bool CanExport() => !IsBusy && Results.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportExcel()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Libro de Excel (*.xlsx)|*.xlsx",
            FileName = Path.GetFileNameWithoutExtension(_doc.FilePath) + ".xlsx",
            InitialDirectory = Path.GetDirectoryName(_doc.FilePath),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ExcelExporter.Export(Results.ToList(), dlg.FileName,
                new ExcelExportOptions { Consolidated = Consolidated, IncludeSourceSheet = IncludeSourceSheet });
            Exported(dlg.FileName);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"No se pudo guardar el archivo (¿está abierto en Excel?).\n{ex.Message}", "Exportar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV separado por punto y coma (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(_doc.FilePath) + ".csv",
            InitialDirectory = Path.GetDirectoryName(_doc.FilePath),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var files = CsvExporter.Export(Results.ToList(), dlg.FileName);
            Exported(files[0], files.Count);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"No se pudo guardar el archivo.\n{ex.Message}", "Exportar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Exported(string path, int fileCount = 1)
    {
        CurrentStep = 4;
        Status = fileCount == 1 ? $"Exportado: {path}" : $"Exportados {fileCount} archivos en {Path.GetDirectoryName(path)}";
        if (MessageBox.Show("Exportación completada. ¿Abrir el archivo?", "Exportar", MessageBoxButton.YesNo,
                MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
