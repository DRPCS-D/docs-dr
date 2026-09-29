using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocsDR.App.Services;
using DocsDR.App.Views;
using DocsDR.Core;
using Microsoft.Win32;

namespace DocsDR.App.ViewModels;

/// <summary>Un documento abierto (una pestaña): visor, selección de texto, anotaciones y operaciones de página.</summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    public const double MinZoom = 0.1, MaxZoom = 6.0;
    public const double PageMargin = 8;
    private const int MaxRenderedPages = 30;

    private readonly LinkedList<PageViewModel> _rendered = new();
    private IReadOnlyList<SearchHit> _hits = [];
    private int _hitIndex = -1;

    public DocumentViewModel(IPdfDocument document)
    {
        Document = document;
        _pages = BuildPages();
        Outline = document.GetOutline();
        _color = ColorOption.Of(PdfColor.Yellow);
        _selectedStamp = StampOption.All[0];
        _ = LoadAnnotationsAsync();
    }

    public IPdfDocument Document { get; }

    /// <summary>null si el documento es de solo lectura.</summary>
    public IPdfEditor? Editor => Document as IPdfEditor;
    public bool CanEdit => Editor is not null;

    public string Title => Path.GetFileName(Document.FilePath);
    public string FilePath => Document.FilePath;
    public string DisplayTitle => IsModified ? "● " + Title : Title;
    public IReadOnlyList<OutlineItem> Outline { get; }
    public bool HasOutline => Outline.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount))]
    private IReadOnlyList<PageViewModel> _pages;

    public int PageCount => Pages.Count;

    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchStatus = "";

    /// <summary>Tamaño del área visible; lo actualiza la vista.</summary>
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>La vista se suscribe para desplazarse a una página (índice base 0) y posición Y opcional (puntos).</summary>
    public event Action<int, double?>? ScrollRequested;

    private List<PageViewModel> BuildPages() =>
        Enumerable.Range(0, Document.PageCount)
            .Select(i =>
            {
                var (w, h) = Document.GetPageSize(i);
                return new PageViewModel(this, i, w, h);
            })
            .ToList();

    // ================= Zoom y navegación =================

    partial void OnZoomChanged(double value)
    {
        foreach (var p in Pages) p.OnZoomChanged();
        OnPropertyChanged(nameof(ZoomPercent));
        // Mantiene la página actual a la vista tras cambiar el tamaño.
        ScrollRequested?.Invoke(CurrentPage - 1, null);
    }

    public string ZoomPercent => $"{Zoom * 100:0}%";

    public void SetZoom(double zoom) => Zoom = Math.Clamp(Math.Round(zoom, 3), MinZoom, MaxZoom);

    [RelayCommand] private void ZoomIn() => SetZoom(Zoom * 1.2);
    [RelayCommand] private void ZoomOut() => SetZoom(Zoom / 1.2);
    [RelayCommand] private void ActualSize() => SetZoom(1.0);

    [RelayCommand]
    private void FitWidth()
    {
        if (ViewportWidth <= 0) return;
        SetZoom((ViewportWidth - 40) / Pages.Max(p => p.PageWidth));
    }

    [RelayCommand]
    private void FitPage()
    {
        if (ViewportWidth <= 0 || ViewportHeight <= 0) return;
        var p = Pages[Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];
        SetZoom(Math.Min((ViewportWidth - 40) / p.PageWidth, (ViewportHeight - 2 * PageMargin - 8) / p.PageHeight));
    }

    public void GoToPage(int pageIndex, double? y = null)
    {
        pageIndex = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        CurrentPage = pageIndex + 1;
        ScrollRequested?.Invoke(pageIndex, y);
    }

    [RelayCommand] private void NextPage() => GoToPage(CurrentPage);
    [RelayCommand] private void PreviousPage() => GoToPage(CurrentPage - 2);
    [RelayCommand] private void FirstPage() => GoToPage(0);
    [RelayCommand] private void LastPage() => GoToPage(Pages.Count - 1);

    // ================= Búsqueda =================

    [RelayCommand]
    private async Task SearchAsync()
    {
        var text = SearchText.Trim();
        foreach (var p in Pages) p.SetHits([]);
        _hits = [];
        _hitIndex = -1;
        if (text.Length == 0) { SearchStatus = ""; return; }

        SearchStatus = "Buscando…";
        var pages = Pages;
        _hits = await Task.Run(() => Document.Search(text));
        if (!ReferenceEquals(pages, Pages)) return; // el documento cambió mientras se buscaba
        foreach (var g in _hits.GroupBy(h => h.PageIndex))
            Pages[g.Key].SetHits(g.Select(h => h.Box));

        if (_hits.Count == 0) SearchStatus = "Sin resultados";
        else NextHit();
    }

    [RelayCommand] private void NextHit() => MoveHit(+1);
    [RelayCommand] private void PreviousHit() => MoveHit(-1);

    private void MoveHit(int delta)
    {
        if (_hits.Count == 0) return;
        _hitIndex = (_hitIndex + delta + _hits.Count) % _hits.Count;
        var hit = _hits[_hitIndex];
        SearchStatus = $"{_hitIndex + 1} de {_hits.Count}";
        GoToPage(hit.PageIndex, hit.Box.Y0);
    }

    /// <summary>Registro LRU de páginas renderizadas para limitar el uso de memoria.</summary>
    internal void TrackRendered(PageViewModel page)
    {
        _rendered.Remove(page);
        _rendered.AddLast(page);
        while (_rendered.Count > MaxRenderedPages)
        {
            _rendered.First!.Value.Evict();
            _rendered.RemoveFirst();
        }
    }

    // ================= Selección de texto (por palabras, dentro de una página) =================

    private PageViewModel? _selPage;
    private int _selAnchor, _selFocus;
    private List<PdfRect> _selRects = [];

    public bool HasSelection => _selPage is not null;

    /// <summary>Selecciona las palabras entre <paramref name="anchor"/> y <paramref name="focus"/> (inclusive, en cualquier orden).</summary>
    public void SetSelection(PageViewModel page, int anchor, int focus)
    {
        if (_selPage is not null && !ReferenceEquals(_selPage, page)) _selPage.SetSelectionRects([]);
        _selPage = page;
        _selAnchor = anchor;
        _selFocus = focus;

        int lo = Math.Min(anchor, focus), hi = Math.Max(anchor, focus);
        var words = page.Words;
        var rects = new List<PdfRect>();
        PdfRect? line = null;
        TextWord? prev = null;
        for (int i = lo; i <= hi && i < words.Count; i++)
        {
            var w = words[i];
            if (prev is not null && !SameLine(prev, w))
            {
                rects.Add(line!.Value);
                line = null;
            }
            line = line is null ? w.Box : line.Value.Union(w.Box);
            prev = w;
        }
        if (line is not null) rects.Add(line.Value);
        _selRects = rects;
        page.SetSelectionRects(rects);
        OnPropertyChanged(nameof(HasSelection));
    }

    public void SelectAll(PageViewModel page)
    {
        if (page.Words.Count > 0) SetSelection(page, 0, page.Words.Count - 1);
    }

    /// <summary>Selecciona todo el renglón de la palabra <paramref name="index"/>.</summary>
    public void SelectLine(PageViewModel page, int index)
    {
        var words = page.Words;
        int lo = index, hi = index;
        while (lo > 0 && SameLine(words[lo - 1], words[index])) lo--;
        while (hi < words.Count - 1 && SameLine(words[hi + 1], words[index])) hi++;
        SetSelection(page, lo, hi);
    }

    public void ClearSelection()
    {
        if (_selPage is null) return;
        _selPage.SetSelectionRects([]);
        _selPage = null;
        _selRects = [];
        OnPropertyChanged(nameof(HasSelection));
    }

    public string GetSelectedText()
    {
        if (_selPage is null) return "";
        var words = _selPage.Words;
        var sb = new System.Text.StringBuilder();
        TextWord? prev = null;
        for (int i = Math.Min(_selAnchor, _selFocus); i <= Math.Max(_selAnchor, _selFocus) && i < words.Count; i++)
        {
            if (prev is not null) sb.Append(SameLine(prev, words[i]) ? ' ' : '\n');
            sb.Append(words[i].Text);
            prev = words[i];
        }
        return sb.ToString();
    }

    /// <summary>Copia la selección al portapapeles; devuelve false si no había nada seleccionado.</summary>
    public bool CopySelection()
    {
        var text = GetSelectedText();
        if (text.Length == 0) return false;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // El portapapeles puede estar bloqueado un instante por otro programa.
            return false;
        }
        SearchStatus = $"Copiado: {text.Length} caracteres";
        return true;
    }

    private static bool SameLine(TextWord a, TextWord b) =>
        Math.Abs(a.Box.CenterY - b.Box.CenterY) <= Math.Max(a.Box.Height, b.Box.Height) * 0.6;

    // ================= Deshacer / rehacer / guardar =================

    private const int MaxUndoEntries = 30;
    private const long MaxUndoBytes = 400L * 1024 * 1024;

    private readonly record struct UndoEntry(byte[] Snapshot, int Version);

    private readonly LinkedList<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();
    private long _undoBytes;
    private int _version, _versionCounter, _savedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private bool _isModified;

    private static string Author => Environment.UserName;

    private void PushUndo(byte[] snapshot, int version)
    {
        _undo.AddLast(new UndoEntry(snapshot, version));
        _undoBytes += snapshot.Length;
        while (_undo.Count > 1 && (_undo.Count > MaxUndoEntries || _undoBytes > MaxUndoBytes))
        {
            _undoBytes -= _undo.First!.Value.Snapshot.Length;
            _undo.RemoveFirst();
        }
    }

    private void NotifyHistory()
    {
        IsModified = _version != _savedVersion;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Ejecuta una modificación protegida por una instantánea: si falla se restaura el estado anterior;
    /// si funciona queda registrada para deshacer. Devuelve true si se aplicó.
    /// </summary>
    private bool Edit(Action<IPdfEditor> action, bool structural = false, params int[] pages)
    {
        if (Editor is not { } ed) return false;
        byte[]? snapshot = null;
        try
        {
            snapshot = ed.CreateSnapshot();
            action(ed);
        }
        catch (OperationCanceledException)
        {
            // La operación decidió no cambiar nada (p. ej. ningún reemplazo cabía): se deja todo como estaba, sin avisos.
            if (snapshot is not null) ed.RestoreSnapshot(snapshot);
            AfterStructuralChange();
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error al modificar el documento");
            if (snapshot is not null)
            {
                try { ed.RestoreSnapshot(snapshot); } catch (Exception rex) { Serilog.Log.Error(rex, "No se pudo restaurar"); }
                AfterStructuralChange();
            }
            MessageBox.Show(ex.Message, "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        PushUndo(snapshot, _version);
        _redo.Clear();
        _version = ++_versionCounter;

        if (structural) AfterStructuralChange();
        else
        {
            foreach (var p in pages.Distinct().Where(p => p >= 0 && p < Pages.Count))
            {
                Pages[p].Refresh();
                InvalidateContentCache(p);
            }
            RefreshAnnotations(pages);
        }
        NotifyHistory();
        return true;
    }

    private bool CanUndo() => _undo.Count > 0;
    private bool CanRedo() => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Editor is not { } ed || _undo.Last is null) return;
        var entry = _undo.Last.Value;
        _undo.RemoveLast();
        _undoBytes -= entry.Snapshot.Length;
        _redo.Push(new UndoEntry(ed.CreateSnapshot(), _version));
        ed.RestoreSnapshot(entry.Snapshot);
        _version = entry.Version;
        AfterStructuralChange();
        NotifyHistory();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (Editor is not { } ed || _redo.Count == 0) return;
        var entry = _redo.Pop();
        PushUndo(ed.CreateSnapshot(), _version);
        ed.RestoreSnapshot(entry.Snapshot);
        _version = entry.Version;
        AfterStructuralChange();
        NotifyHistory();
    }

    /// <summary>Cambió el número o tamaño de páginas (o se restauró una versión): se reconstruye todo.</summary>
    private void AfterStructuralChange()
    {
        _rendered.Clear();
        ClearSelection();
        _hits = [];
        _hitIndex = -1;
        SearchStatus = "";
        SelectedPageIndexes = [];
        ClearContentCaches();
        SelectedImage = null;
        _outlinePage = null;
        Pages = BuildPages();
        CurrentPage = Math.Clamp(CurrentPage, 1, Math.Max(1, Pages.Count));
        SelectAnnotation(null);
        RefreshAnnotations(null);
        ScrollRequested?.Invoke(CurrentPage - 1, null);
    }

    /// <summary>Guarda en la ruta actual. Devuelve false si falla (el usuario ya fue avisado).</summary>
    public bool Save() => SaveTo(null);

    public bool SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = Title,
            InitialDirectory = Path.GetDirectoryName(FilePath),
        };
        return dlg.ShowDialog() == true && SaveTo(dlg.FileName);
    }

    private bool SaveTo(string? path)
    {
        if (Editor is not { } ed) return false;
        try
        {
            ed.Save(path ?? FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"No se pudo guardar el archivo (¿está abierto en otro programa o es de solo lectura?).\n{ex.Message}",
                "Guardar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _savedVersion = _version;
        NotifyHistory();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DisplayTitle));
        SearchStatus = "Guardado";
        return true;
    }

    // ================= Herramientas de anotación =================

    public static IReadOnlyList<ColorOption> Colors => ColorOption.All;
    public static IReadOnlyList<StampOption> Stamps => StampOption.All;
    public static IReadOnlyList<double> StrokeWidths { get; } = [1, 2, 3, 5];

    [ObservableProperty] private AnnotTool _tool = AnnotTool.Select;
    [ObservableProperty] private ColorOption _color;
    [ObservableProperty] private double _strokeWidth = 2;
    [ObservableProperty] private StampOption _selectedStamp;

    private (byte[] Bytes, double Aspect)? _pendingImage;
    private bool _syncingColor;

    public bool IsMarkupTool => Tool is AnnotTool.Highlight or AnnotTool.Underline or AnnotTool.StrikeOut;

    [RelayCommand]
    private void SetTool(AnnotTool tool) => Tool = Tool == tool ? AnnotTool.Select : tool;

    partial void OnToolChanged(AnnotTool value)
    {
        OnPropertyChanged(nameof(IsMarkupTool));
        OnPropertyChanged(nameof(ToolHint));
        SelectAnnotation(null);
        if (value != AnnotTool.Select && value != AnnotTool.Highlight && value != AnnotTool.Underline && value != AnnotTool.StrikeOut)
            ClearSelection();

        // Cada familia de herramientas recuerda su color: amarillo para marcar, rojo para dibujar.
        _syncingColor = true;
        Color = ColorOption.Of(IsMarkupTool || value == AnnotTool.Select ? _markupColor : _drawColor);
        _syncingColor = false;

        if (value != AnnotTool.EditObjects) SelectImage(null);
        if (value == AnnotTool.Image && !PickImage()) Tool = AnnotTool.Select;
        else if (value == AnnotTool.AddPageImage && !PickPageImage()) Tool = AnnotTool.Select;
        else if (IsMarkupTool && HasSelection) ApplyMarkupToSelection(); // texto ya seleccionado + herramienta
    }

    private PdfColor _markupColor = PdfColor.Yellow, _drawColor = PdfColor.Red;

    partial void OnColorChanged(ColorOption value)
    {
        if (_syncingColor) return;
        if (IsMarkupTool || Tool == AnnotTool.Select) _markupColor = value.Value; else _drawColor = value.Value;
        // Con una anotación seleccionada, el color se aplica a ella.
        if (SelectedAnnotation is { } a)
            Edit(ed => ed.UpdateAnnotation(a.PageIndex, a.Id, null, value.Value), false, a.PageIndex);
    }

    private bool PickImage()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "Elegir imagen o firma",
        };
        if (dlg.ShowDialog() != true) return false;
        try
        {
            _pendingImage = StampFactory.LoadImage(dlg.FileName);
            SearchStatus = "Haz clic en la página donde colocar la imagen";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo leer la imagen:\n{ex.Message}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private PdfColor ToolColor => Color.Value;

    /// <summary>Aplica resaltado/subrayado/tachado a la selección de texto actual y la limpia.</summary>
    public void ApplyMarkupToSelection()
    {
        if (_selPage is null || _selRects.Count == 0 || !IsMarkupTool) return;
        var kind = Tool switch
        {
            AnnotTool.Underline => AnnotationKind.Underline,
            AnnotTool.StrikeOut => AnnotationKind.StrikeOut,
            _ => AnnotationKind.Highlight,
        };
        var page = _selPage.Index;
        var rects = _selRects.ToList();
        var color = ToolColor;
        ClearSelection();
        Edit(ed => ed.AddTextMarkup(page, kind, rects, color, Author), false, page);
    }

    private void AddAndSelect(int pageIndex, Func<IPdfEditor, int> add, AnnotTool? nextTool = null)
    {
        int id = 0;
        if (!Edit(ed => id = add(ed), false, pageIndex)) return;
        if (nextTool is { } t) Tool = t;
        var created = _annots.GetValueOrDefault(pageIndex)?.FirstOrDefault(a => a.Id == id);
        if (created is not null) SelectAnnotation(created);
    }

    public void AddNoteAt(PageViewModel page, PdfPoint at)
    {
        var text = Dialogs.AskText("Nota", "Escribe el texto de la nota:");
        if (text is null) return;
        var color = ToolColor;
        AddAndSelect(page.Index, ed => ed.AddNote(page.Index, at, text, color, Author), AnnotTool.Select);
    }

    public void AddFreeTextIn(PageViewModel page, PdfRect box)
    {
        var text = Dialogs.AskText("Cuadro de texto", "Escribe el texto:");
        if (string.IsNullOrEmpty(text)) return;
        var color = ToolColor;
        AddAndSelect(page.Index, ed => ed.AddFreeText(page.Index, box, text, 12, color, Author), AnnotTool.Select);
    }

    public void AddInkStroke(PageViewModel page, IReadOnlyList<PdfPoint> stroke)
    {
        if (stroke.Count < 2) return;
        var (color, width) = (ToolColor, StrokeWidth);
        AddAndSelect(page.Index, ed => ed.AddInk(page.Index, stroke, color, width, Author));
    }

    public void AddShape(PageViewModel page, AnnotationKind kind, PdfPoint from, PdfPoint to)
    {
        var (color, width) = (ToolColor, StrokeWidth);
        AddAndSelect(page.Index, ed => ed.AddShape(page.Index, kind, from, to, color, width, Author));
    }

    /// <summary>Coloca el sello elegido (o la imagen escogida) centrado en el punto indicado.</summary>
    public void PlaceStamp(PageViewModel page, PdfPoint center)
    {
        byte[] bytes;
        double aspect, height;
        if (Tool == AnnotTool.Image && _pendingImage is { } img)
        {
            (bytes, aspect) = img;
            height = Math.Min(120, page.PageWidth * 0.4 / aspect);
        }
        else
        {
            (bytes, aspect) = StampFactory.Render(SelectedStamp);
            height = StampFactory.HeightPt;
        }
        double width = height * aspect;
        var box = new PdfRect(center.X - width / 2, center.Y - height / 2, center.X + width / 2, center.Y + height / 2);
        AddAndSelect(page.Index, ed => ed.AddStamp(page.Index, box, bytes, Author), AnnotTool.Select);
    }

    // ================= Anotaciones existentes =================

    private Dictionary<int, IReadOnlyList<AnnotationInfo>> _annots = [];
    private int _annotLoadVersion;

    public ObservableCollection<AnnotationItem> Annotations { get; } = [];
    public bool HasAnnotations => Annotations.Count > 0;

    [ObservableProperty] private AnnotationInfo? _selectedAnnotation;
    [ObservableProperty] private AnnotationItem? _selectedAnnotationItem;

    private async Task LoadAnnotationsAsync()
    {
        int version = ++_annotLoadVersion;
        var doc = Document;
        if (Editor is not { } ed) return;
        var loaded = await Task.Run(() => Enumerable.Range(0, doc.PageCount)
            .ToDictionary(i => i, i => ed.GetAnnotations(i)));
        if (version != _annotLoadVersion) return; // llegó una actualización más reciente
        _annots = loaded;
        RebuildAnnotationList();
    }

    /// <summary>Vuelve a leer las anotaciones de <paramref name="pages"/> (o de todo el documento si es null).</summary>
    private void RefreshAnnotations(int[]? pages)
    {
        if (Editor is not { } ed) return;
        _annotLoadVersion++; // invalida una carga asíncrona en curso
        if (pages is null)
        {
            _annots = Enumerable.Range(0, Document.PageCount).ToDictionary(i => i, i => ed.GetAnnotations(i));
        }
        else
        {
            foreach (var p in pages.Distinct().Where(p => p >= 0 && p < Document.PageCount))
                _annots[p] = ed.GetAnnotations(p);
        }
        RebuildAnnotationList();

        // La anotación seleccionada puede haber cambiado (o desaparecido).
        if (SelectedAnnotation is { } sel)
            SelectAnnotation(_annots.GetValueOrDefault(sel.PageIndex)?.FirstOrDefault(a => a.Id == sel.Id));
    }

    private void RebuildAnnotationList()
    {
        Annotations.Clear();
        foreach (var a in _annots.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value.OrderBy(a => a.Box.Y0)))
            Annotations.Add(new AnnotationItem(a));
        OnPropertyChanged(nameof(HasAnnotations));
        SelectedAnnotationItem = SelectedAnnotation is { } s ? Annotations.FirstOrDefault(i => i.Info.Id == s.Id) : null;
    }

    /// <summary>Anotación bajo el punto (coordenadas de página). Las de marcado solo cuentan si se pide.</summary>
    public AnnotationInfo? HitTestAnnotation(int pageIndex, double x, double y, bool includeMarkup)
    {
        if (!_annots.TryGetValue(pageIndex, out var list)) return null;
        return list.Where(a => (includeMarkup || !a.IsMarkup) && a.Box.Inflate(3).Contains(x, y))
                   .OrderBy(a => a.Box.Width * a.Box.Height)
                   .FirstOrDefault();
    }

    public void SelectAnnotation(AnnotationInfo? info)
    {
        SelectedAnnotation = info;
        if (info is not null) SelectedImage = null; // una sola cosa seleccionada a la vez
        ShowOutline(info?.PageIndex, info?.Box, handles: false);
        SelectedAnnotationItem = info is null ? null : Annotations.FirstOrDefault(i => i.Info.Id == info.Id);
        DeleteSelectedAnnotationCommand.NotifyCanExecuteChanged();
        EditSelectedAnnotationTextCommand.NotifyCanExecuteChanged();
    }

    private PageViewModel? _outlinePage;

    /// <summary>Contorno punteado (con asas si se piden) de lo que está seleccionado; solo hay uno en todo el documento.</summary>
    private void ShowOutline(int? pageIndex, PdfRect? box, bool handles)
    {
        _outlinePage?.SetAnnotationBox(null);
        _outlinePage = null;
        if (pageIndex is int p && box is { } b && p >= 0 && p < Pages.Count)
        {
            _outlinePage = Pages[p];
            _outlinePage.SetAnnotationBox(b, handles);
        }
    }

    partial void OnSelectedAnnotationItemChanged(AnnotationItem? value)
    {
        // Selección hecha desde el panel de comentarios: se navega a la anotación.
        if (value is null || value.Info.Id == SelectedAnnotation?.Id) return;
        SelectAnnotation(value.Info);
        GoToPage(value.Info.PageIndex, value.Info.Box.Y0);
    }

    private bool HasSelectedAnnotation() => SelectedAnnotation is not null && CanEdit;

    [RelayCommand(CanExecute = nameof(HasSelectedAnnotation))]
    private void DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation is not { } a) return;
        SelectAnnotation(null);
        Edit(ed => ed.DeleteAnnotation(a.PageIndex, a.Id), false, a.PageIndex);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAnnotation))]
    public void EditSelectedAnnotationText()
    {
        if (SelectedAnnotation is not { } a) return;
        var text = Dialogs.AskText("Comentario", "Texto de la anotación:", a.Content);
        if (text is null || text == a.Content) return;
        Edit(ed => ed.UpdateAnnotation(a.PageIndex, a.Id, text, null), false, a.PageIndex);
    }

    public void MoveSelectedAnnotation(double dx, double dy)
    {
        if (SelectedAnnotation is not { CanMove: true } a || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)) return;
        Edit(ed => ed.MoveAnnotation(a.PageIndex, a.Id, dx, dy), false, a.PageIndex);
    }

    // ================= Operaciones de página =================

    /// <summary>Páginas marcadas en el panel de miniaturas (la vista lo mantiene al día).</summary>
    public IReadOnlyList<int> SelectedPageIndexes { get; set; } = [];

    private IReadOnlyList<int> TargetPages =>
        SelectedPageIndexes.Count > 0 ? SelectedPageIndexes.Order().ToList() : [Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];

    private static string PagesLabel(IReadOnlyList<int> pages) =>
        pages.Count == 1 ? $"la página {pages[0] + 1}" : $"{pages.Count} páginas";

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RotateLeft() => Edit(ed => ed.RotatePages(TargetPages, -90), structural: true);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RotateRight() => Edit(ed => ed.RotatePages(TargetPages, 90), structural: true);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeletePages()
    {
        var pages = TargetPages;
        if (pages.Count >= Pages.Count)
        {
            MessageBox.Show("No se pueden eliminar todas las páginas del documento.", "Eliminar páginas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"¿Eliminar {PagesLabel(pages)}? Podrás deshacerlo con Ctrl+Z.", "Eliminar páginas",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Edit(ed => ed.DeletePages(pages), structural: true);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ExtractPages()
    {
        if (Editor is not { } ed) return;
        var pages = TargetPages;
        var dlg = new SaveFileDialog
        {
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(Title) + "_extracto.pdf",
            InitialDirectory = Path.GetDirectoryName(FilePath),
            Title = $"Extraer {PagesLabel(pages)}",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ed.ExtractPages(pages, dlg.FileName);
            SearchStatus = $"Extraídas {pages.Count} página(s)";
        }
        catch (Exception ex) { ShowError("No se pudo extraer", ex); }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void InsertBlankPage()
    {
        int last = TargetPages[^1];
        var (w, h) = Document.GetPageSize(last);
        if (Edit(ed => ed.InsertBlankPage(last + 1, w, h), structural: true)) GoToPage(last + 1);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void InsertFromPdf()
    {
        var dlg = new OpenFileDialog { Filter = "Documentos PDF (*.pdf)|*.pdf", Multiselect = true, Title = "Insertar páginas desde…" };
        if (dlg.ShowDialog() != true) return;
        int at = TargetPages[^1] + 1;
        int inserted = 0;
        if (Edit(ed =>
        {
            int position = at;
            foreach (var file in dlg.FileNames)
            {
                int count;
                try { count = ed.InsertPagesFrom(file, position); }
                catch (PdfPasswordRequiredException)
                {
                    var pwd = PasswordDialog.Ask(Path.GetFileName(file));
                    if (pwd is null) continue;
                    count = ed.InsertPagesFrom(file, position, pwd);
                }
                position += count;
                inserted += count;
            }
        }, structural: true))
        {
            GoToPage(at);
            SearchStatus = $"Insertadas {inserted} página(s)";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SplitDocument()
    {
        if (Editor is not { } ed) return;
        var groups = Dialogs.AskSplit(Pages.Count);
        if (groups is null) return;
        var dlg = new OpenFolderDialog { Title = "Carpeta de destino", InitialDirectory = Path.GetDirectoryName(FilePath) };
        if (dlg.ShowDialog() != true) return;
        var name = Path.GetFileNameWithoutExtension(Title);
        try
        {
            for (int i = 0; i < groups.Count; i++)
                ed.ExtractPages(groups[i], Path.Combine(dlg.FolderName, $"{name}_{i + 1}.pdf"));
            SearchStatus = $"Se crearon {groups.Count} archivo(s)";
            MessageBox.Show($"Se crearon {groups.Count} archivo(s) en:\n{dlg.FolderName}", "Dividir documento", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("No se pudo dividir", ex); }
    }

    /// <summary>Mueve las páginas indicadas junto a la página <paramref name="target"/> (arrastrar y soltar).</summary>
    public void MovePages(IReadOnlyList<int> moving, int target)
    {
        var set = moving.ToHashSet();
        if (set.Count == 0 || set.Contains(target)) return;
        var rest = Enumerable.Range(0, Pages.Count).Where(i => !set.Contains(i)).ToList();
        int pos = rest.IndexOf(target);
        // Hacia arriba se coloca antes de la página destino; hacia abajo, después.
        if (target > moving.Min()) pos++;
        var order = rest.Take(pos).Concat(moving.Order()).Concat(rest.Skip(pos)).ToList();
        if (Edit(ed => ed.ReorderPages(order), structural: true))
            GoToPage(order.IndexOf(moving.Min()));
    }

    private static void ShowError(string message, Exception ex)
    {
        Serilog.Log.Error(ex, message);
        MessageBox.Show($"{message}:\n{ex.Message}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Dispose() => Document.Dispose();
}
