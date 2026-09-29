using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.ComponentModel;
using DocsDR.App.ViewModels;
using DocsDR.Core;

namespace DocsDR.App.Views;

public partial class DocumentView : UserControl
{
    private const string PagesFormat = "docsdr/pages";
    private const double GrabDistance = 6; // puntos PDF: a esta distancia de una palabra se puede iniciar la selección
    private const double DefaultTextWidth = 170, DefaultTextHeight = 44;

    private enum Gesture { None, Text, Shape, Ink, MoveAnnotation, MoveImage, ResizeImage }

    private ScrollViewer? _scroll;
    private DocumentViewModel? _vm;

    // Gesto en curso sobre una página
    private Gesture _gesture;
    private PageViewModel? _gPage;
    private Grid? _gGrid;
    private Point _startPx;            // punto inicial en píxeles de la página
    private int _dragAnchor;           // palabra donde empezó la selección de texto
    private bool _moved;
    private AnnotationInfo? _pendingMarkupHit; // resaltado bajo un clic simple: se selecciona al soltar si no hubo arrastre
    private Shape? _preview;
    private Polyline? _inkPreview;
    private readonly List<Point> _ink = [];
    private int _resizeCorner;         // 0 = arriba-izquierda, 1 = arriba-derecha, 2 = abajo-izquierda, 3 = abajo-derecha
    private Rect _resizeRectPx;

    // Editor de texto flotante sobre la página
    private TextEditSession? _session;
    private Grid? _editorGrid;
    private Border? _editorHost;
    private TextBox? _editBox;

    // Arrastre de miniaturas
    private Point _thumbStart;
    private bool _thumbDragArmed;

    public DocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DocumentViewModel);
        Loaded += (_, _) =>
        {
            _scroll ??= PagesHost.Template.FindName("Scroll", PagesHost) as ScrollViewer;
            if (_scroll is not null)
            {
                _scroll.ScrollChanged -= OnScrollChanged;
                _scroll.ScrollChanged += OnScrollChanged;
            }
            UpdateViewport();
            ApplyPendingPage();
        };
    }

    /// <summary>Al reabrir la sesión anterior, va a la página en la que se había quedado el documento.</summary>
    private void ApplyPendingPage()
    {
        if (_vm?.PendingPage is not int page) return;
        _vm.PendingPage = null;
        _vm.GoToPage(page);
    }

    private void Attach(DocumentViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.ScrollRequested -= OnScrollRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.PagesSelectionRequested -= OnPagesSelectionRequested;
        }
        CloseTextEditor();
        _vm = vm;
        if (_vm is not null)
        {
            _vm.ScrollRequested += OnScrollRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.PagesSelectionRequested += OnPagesSelectionRequested;
            if (IsLoaded) ApplyPendingPage();
        }
    }

    // ================= Desplazamiento y zoom =================

    private double ItemHeight(PageViewModel p) => p.DisplayHeight + 2 * DocumentViewModel.PageMargin;

    private void OnScrollRequested(int pageIndex, double? y)
    {
        if (_vm is null) return;
        // Espera a que el layout refleje el nuevo zoom antes de desplazarse.
        Dispatcher.BeginInvoke(() =>
        {
            if (_scroll is null || _vm is null || pageIndex >= _vm.Pages.Count) return;
            double offset = 0;
            for (int i = 0; i < pageIndex; i++) offset += ItemHeight(_vm.Pages[i]);
            if (y is double py) offset += DocumentViewModel.PageMargin + py * _vm.Zoom - 60;
            _scroll.ScrollToVerticalOffset(Math.Max(0, offset));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || _scroll is null) return;
        double probe = _scroll.VerticalOffset + _scroll.ViewportHeight / 3;
        double acc = 0;
        for (int i = 0; i < _vm.Pages.Count; i++)
        {
            acc += ItemHeight(_vm.Pages[i]);
            if (acc > probe) { _vm.CurrentPage = i + 1; return; }
        }
        _vm.CurrentPage = _vm.Pages.Count;
    }

    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();

    private void UpdateViewport()
    {
        if (_vm is null) return;
        _vm.ViewportWidth = PagesHost.ActualWidth;
        _vm.ViewportHeight = PagesHost.ActualHeight;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null || Keyboard.Modifiers != ModifierKeys.Control) return;
        _vm.SetZoom(_vm.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void OnOutlineClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: OutlineItem item }) _vm?.GoToPage(item.PageIndex);
    }

    // ================= Miniaturas: selección, navegación y reordenar =================
    // Sirven a dos listas: el panel lateral (una columna) y la cuadrícula de «Organizar páginas».

    private bool IsGrid(ListBox list) => ReferenceEquals(list, OrganizeGrid);

    private static ListBoxItem? ItemAt(ListBox list, object? source) =>
        source is DependencyObject d ? ItemsControl.ContainerFromElement(list, d) as ListBoxItem : null;

    private bool _syncingSelection;

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(Thumbnails, OrganizeGrid);

    private void OnOrganizeSelectionChanged(object sender, SelectionChangedEventArgs e) => SyncSelection(OrganizeGrid, Thumbnails);

    /// <summary>Guarda las páginas marcadas en la lista activa y las refleja en la otra.</summary>
    private void SyncSelection(ListBox from, ListBox to)
    {
        if (_vm is null || _syncingSelection) return;
        var picked = from.SelectedItems.OfType<PageViewModel>().ToList();
        _vm.SelectedPageIndexes = picked.Select(p => p.Index).Order().ToList();
        _syncingSelection = true;
        try
        {
            to.SelectedItems.Clear();
            foreach (var p in picked) to.SelectedItems.Add(p);
        }
        finally { _syncingSelection = false; }
    }

    /// <summary>Tras mover, duplicar o girar la lista se reconstruye: se vuelven a marcar las páginas afectadas.</summary>
    private void OnPagesSelectionRequested(IReadOnlyList<int> pages)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm is null) return;
            _syncingSelection = true;
            try
            {
                foreach (var list in new[] { Thumbnails, OrganizeGrid })
                {
                    list.SelectedItems.Clear();
                    foreach (var i in pages.Where(i => i >= 0 && i < _vm.Pages.Count)) list.SelectedItems.Add(_vm.Pages[i]);
                }
            }
            finally { _syncingSelection = false; }
            _vm.SelectedPageIndexes = pages.Order().ToList();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        // Con Ctrl/Mayús se está armando una selección múltiple: no se navega.
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (ItemAt(Thumbnails, e.OriginalSource)?.DataContext is PageViewModel p) _vm?.GoToPage(p.Index);
    }

    private void OnOrganizeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(OrganizeGrid, e.OriginalSource)?.DataContext is not PageViewModel p) return;
        _vm?.GoToPage(p.Index);
        (Window.GetWindow(this) as MainWindow)?.ShowViewer();
        e.Handled = true;
    }

    private void OnOrganizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm?.DeletePagesCommand.CanExecute(null) == true)
        {
            _vm.DeletePagesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOrganizeVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) _vm?.ReleaseGridThumbnails();
    }

    private void OnThumbnailMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        _thumbStart = e.GetPosition(list);
        _thumbDragArmed = ItemAt(list, e.OriginalSource) is not null;
    }

    private void OnThumbnailMouseMove(object sender, MouseEventArgs e)
    {
        var list = (ListBox)sender;
        if (!_thumbDragArmed || e.LeftButton != MouseButtonState.Pressed || _vm is not { CanEdit: true }) return;
        var d = e.GetPosition(list) - _thumbStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _thumbDragArmed = false;
        if (ItemAt(list, e.OriginalSource)?.DataContext is not PageViewModel page) return;
        // Arrastrar una página que no estaba seleccionada mueve solo esa.
        var indexes = list.SelectedItems.Contains(page)
            ? list.SelectedItems.OfType<PageViewModel>().Select(p => p.Index).ToArray()
            : [page.Index];
        DragDrop.DoDragDrop(list, new DataObject(PagesFormat, indexes), DragDropEffects.Move | DragDropEffects.Copy);
        HideDropLine();
    }

    private static string[] DroppedPdfs(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

    private static bool CtrlHeld(DragEventArgs e) => e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);

    private void OnThumbnailDragOver(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        if (_vm is null) return;

        if (e.Data.GetData(PagesFormat) is int[] indexes)
        {
            bool copy = CtrlHeld(e);
            e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            int gap = DropGap(list, e, out var line);
            if (!copy && _vm.IsNoOpMove(indexes, gap)) HideDropLine();
            else ShowDropLine(list, line);
        }
        else if (DroppedPdfs(e).Length > 0)
        {
            // Un PDF soltado sobre las miniaturas se inserta en ese punto (fuera de ellas la ventana lo abre en una pestaña).
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            DropGap(list, e, out var line);
            ShowDropLine(list, line);
        }
        else return;
        AutoScrollThumbnails(list, e.GetPosition(list));
    }

    private void OnThumbnailDragLeave(object sender, DragEventArgs e) => HideDropLine();

    private void OnThumbnailDrop(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        HideDropLine();
        if (_vm is null) return;
        if (e.Data.GetData(PagesFormat) is int[] indexes)
        {
            int gap = DropGap(list, e, out _);
            if (CtrlHeld(e)) _vm.DuplicatePagesToGap(indexes, gap);
            else _vm.MovePagesToGap(indexes, gap);
            e.Handled = true;
        }
        else if (DroppedPdfs(e) is { Length: > 0 } pdfs)
        {
            _vm.InsertPdfFilesAtGap(pdfs, DropGap(list, e, out _));
            e.Handled = true;
        }
    }

    /// <summary>
    /// Hueco de destino (0 = antes de la primera página, Count = tras la última) y la línea que lo marca, en coordenadas de la
    /// lista. Se decide por la miniatura más cercana al cursor y por qué mitad de ella se apunta.
    /// </summary>
    private int DropGap(ListBox list, DragEventArgs e, out Rect line)
    {
        line = Rect.Empty;
        int count = list.Items.Count;
        if (count == 0) return 0;

        var mouse = e.GetPosition(list);
        ListBoxItem? best = null;
        double bestDist = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c || c.ActualWidth == 0) continue;
            var r = new Rect(c.TranslatePoint(new Point(0, 0), list), new Size(c.ActualWidth, c.ActualHeight));
            double dx = Math.Max(Math.Max(r.Left - mouse.X, 0), mouse.X - r.Right);
            double dy = Math.Max(Math.Max(r.Top - mouse.Y, 0), mouse.Y - r.Bottom);
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = c; }
        }
        if (best?.DataContext is not PageViewModel page) return count;

        var box = new Rect(best.TranslatePoint(new Point(0, 0), list), new Size(best.ActualWidth, best.ActualHeight));
        bool grid = IsGrid(list);
        bool before = grid ? mouse.X < box.Left + box.Width / 2 : mouse.Y < box.Top + box.Height / 2;
        const double thickness = 3;
        if (grid)
        {
            double x = before ? box.Left : box.Right;
            line = new Rect(x - thickness / 2, box.Top + 2, thickness, Math.Max(0, box.Height - 4));
        }
        else
        {
            double y = before ? box.Top : box.Bottom;
            line = new Rect(4, y - thickness / 2, Math.Max(0, list.ActualWidth - 8), thickness);
        }
        return page.Index + (before ? 0 : 1);
    }

    // Indicador de destino: una línea (horizontal en el panel lateral, vertical en la cuadrícula) entre miniaturas.
    private sealed class DropLineAdorner(UIElement target) : System.Windows.Documents.Adorner(target)
    {
        public Rect Line { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            if (Line.IsEmpty) return;
            var brush = SystemColors.HighlightBrush;
            dc.DrawRectangle(brush, null, Line);
            // Tiradores en los extremos para que se vea aun sobre miniaturas blancas.
            bool vertical = Line.Height > Line.Width;
            var a = vertical ? new Point(Line.Left + Line.Width / 2, Line.Top + 3) : new Point(Line.Left + 3, Line.Top + Line.Height / 2);
            var b = vertical ? new Point(Line.Left + Line.Width / 2, Line.Bottom - 3) : new Point(Line.Right - 3, Line.Top + Line.Height / 2);
            dc.DrawEllipse(brush, null, a, 4, 4);
            dc.DrawEllipse(brush, null, b, 4, 4);
        }
    }

    private DropLineAdorner? _dropLine;
    private ListBox? _dropList;

    private void ShowDropLine(ListBox list, Rect line)
    {
        if (line.IsEmpty) { HideDropLine(); return; }
        if (_dropLine is not null && !ReferenceEquals(_dropList, list)) HideDropLine();
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(list);
        if (layer is null) return;
        if (_dropLine is null)
        {
            _dropLine = new DropLineAdorner(list) { IsHitTestVisible = false };
            _dropList = list;
            layer.Add(_dropLine);
        }
        _dropLine.Line = line;
        _dropLine.InvalidateVisual();
    }

    private void HideDropLine()
    {
        if (_dropLine is null) return;
        if (_dropList is not null) System.Windows.Documents.AdornerLayer.GetAdornerLayer(_dropList)?.Remove(_dropLine);
        _dropLine = null;
        _dropList = null;
    }

    /// <summary>Desplaza la lista cuando se arrastra cerca del borde superior o inferior.</summary>
    private static void AutoScrollThumbnails(ListBox list, Point p)
    {
        const double edge = 36, step = 18;
        if (FindScrollViewer(list) is not { } sv) return;
        if (p.Y < edge) sv.ScrollToVerticalOffset(sv.VerticalOffset - step);
        else if (p.Y > list.ActualHeight - edge) sv.ScrollToVerticalOffset(sv.VerticalOffset + step);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    // ================= Ratón sobre la página =================

    private PdfPoint ToPage(Point px) => new(px.X / _vm!.Zoom, px.Y / _vm.Zoom);

    private static Canvas? DrawLayerOf(Grid grid) => grid.Children.OfType<Canvas>().FirstOrDefault();

    private void OnPageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not Grid { Tag: PageViewModel page } grid) return;
        if (_editorHost is not null)
        {
            if (IsInsideEditor(e.OriginalSource)) return; // clic dentro del editor: es suyo
            CommitEditorIfChanged();                      // clic fuera: se aplica lo escrito (si hay cambios)
        }
        Focus(); // para recibir Ctrl+C / Ctrl+A / Supr
        var px = e.GetPosition(grid);
        var pt = ToPage(px);

        switch (_vm.Tool)
        {
            case AnnotTool.EditText:
                e.Handled = true;
                if (_vm.BeginTextEdit(page, pt) is { } editSession) ShowTextEditor(grid, editSession);
                break;
            case AnnotTool.AddText:
                e.Handled = true;
                ShowTextEditor(grid, _vm.BeginNewText(page, pt));
                break;
            case AnnotTool.AddPageImage:
                e.Handled = true;
                _vm.PlaceContentImage(page, pt);
                break;
            case AnnotTool.EditObjects:
                e.Handled = true;
                HandleImageClick(page, grid, px, pt);
                break;
            case AnnotTool.Select or AnnotTool.Highlight or AnnotTool.Underline or AnnotTool.StrikeOut:
                if (_vm.Tool == AnnotTool.Select && TryAnnotationClick(e, page, grid, px, pt)) return;
                BeginTextSelection(e, page, grid, pt);
                break;
            case AnnotTool.Note:
                e.Handled = true;
                _vm.AddNoteAt(page, pt);
                break;
            case AnnotTool.Stamp or AnnotTool.Image:
                e.Handled = true;
                _vm.PlaceStamp(page, pt);
                break;
            case AnnotTool.Ink:
                BeginGesture(Gesture.Ink, page, grid, px);
                e.Handled = true;
                break;
            default: // texto libre, rectángulo, elipse, línea, flecha
                BeginGesture(Gesture.Shape, page, grid, px);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Clic con la herramienta Seleccionar: ¿hay una anotación (no de marcado) bajo el puntero?</summary>
    private bool TryAnnotationClick(MouseButtonEventArgs e, PageViewModel page, Grid grid, Point px, PdfPoint pt)
    {
        _pendingMarkupHit = null;
        var hit = _vm!.HitTestAnnotation(page.Index, pt.X, pt.Y, includeMarkup: false);
        if (hit is null)
        {
            _vm.SelectAnnotation(null);
            // Un resaltado bajo el clic solo se selecciona si no se arrastra (arrastrar selecciona texto).
            _pendingMarkupHit = _vm.HitTestAnnotation(page.Index, pt.X, pt.Y, includeMarkup: true);
            return false;
        }

        _vm.ClearSelection();
        _vm.SelectAnnotation(hit);
        e.Handled = true;
        if (e.ClickCount >= 2) { _vm.EditSelectedAnnotationText(); return true; }
        if (hit.CanMove) BeginGesture(Gesture.MoveAnnotation, page, grid, px);
        return true;
    }

    private void BeginTextSelection(MouseButtonEventArgs e, PageViewModel page, Grid grid, PdfPoint pt)
    {
        int hit = page.NearestWord(pt.X, pt.Y, GrabDistance);
        if (hit < 0)
        {
            _vm!.ClearSelection();
            return;
        }

        switch (e.ClickCount)
        {
            case 1:
                _dragAnchor = hit;
                BeginGesture(Gesture.Text, page, grid, e.GetPosition(grid));
                _vm!.SetSelection(page, hit, hit);
                break;
            case 2:
                _vm!.SetSelection(page, hit, hit); // una palabra
                if (_vm.IsMarkupTool) _vm.ApplyMarkupToSelection();
                break;
            default:
                _vm!.SelectLine(page, hit); // triple clic: renglón
                if (_vm.IsMarkupTool) _vm.ApplyMarkupToSelection();
                break;
        }
        e.Handled = true;
    }

    private void BeginGesture(Gesture gesture, PageViewModel page, Grid grid, Point px)
    {
        _gesture = gesture;
        _gPage = page;
        _gGrid = grid;
        _startPx = px;
        _moved = false;
        grid.CaptureMouse();

        var layer = DrawLayerOf(grid);
        if (layer is null) return;
        layer.Children.Clear();
        double z = _vm!.Zoom;
        var stroke = _vm.Color.Brush;
        double thickness = Math.Max(1, _vm.StrokeWidth * z);

        switch (gesture)
        {
            case Gesture.Shape:
                _preview = _vm.Tool switch
                {
                    AnnotTool.Ellipse => new Ellipse { Stroke = stroke, StrokeThickness = thickness },
                    AnnotTool.Line or AnnotTool.Arrow => new Line { Stroke = stroke, StrokeThickness = thickness, X1 = px.X, Y1 = px.Y, X2 = px.X, Y2 = px.Y },
                    AnnotTool.FreeText => new Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5, StrokeDashArray = [4, 3] },
                    _ => new Rectangle { Stroke = stroke, StrokeThickness = thickness },
                };
                layer.Children.Add(_preview);
                break;
            case Gesture.Ink:
                _ink.Clear();
                _ink.Add(px);
                _inkPreview = new Polyline
                {
                    Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Points = [px],
                };
                layer.Children.Add(_inkPreview);
                break;
            case Gesture.MoveAnnotation when _vm.SelectedAnnotation is { } a:
                AddBoxPreview(layer, a.Box, z);
                break;
            case Gesture.MoveImage or Gesture.ResizeImage when _vm.SelectedImage is { } im:
                AddBoxPreview(layer, im.Box, z);
                break;
        }
    }

    private void AddBoxPreview(Canvas layer, PdfRect box, double z)
    {
        _preview = new Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5, StrokeDashArray = [4, 3], Width = box.Width * z, Height = box.Height * z };
        Canvas.SetLeft(_preview, box.X0 * z);
        Canvas.SetTop(_preview, box.Y0 * z);
        layer.Children.Add(_preview);
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm is null || sender is not Grid { Tag: PageViewModel page } grid) return;
        var px = e.GetPosition(grid);

        if (_gesture != Gesture.None && ReferenceEquals(grid, _gGrid))
        {
            if (e.LeftButton == MouseButtonState.Pressed) ContinueGesture(px, page);
            return;
        }

        var pt = ToPage(px);
        grid.Cursor = _vm.Tool switch
        {
            AnnotTool.Select or AnnotTool.Highlight or AnnotTool.Underline or AnnotTool.StrikeOut =>
                _vm.Tool == AnnotTool.Select && _vm.HitTestAnnotation(page.Index, pt.X, pt.Y, false) is not null ? Cursors.Hand
                : page.NearestWord(pt.X, pt.Y, 1) >= 0 ? Cursors.IBeam : Cursors.Arrow,
            AnnotTool.EditText or AnnotTool.AddText =>
                page.NearestWord(pt.X, pt.Y, 1) >= 0 || _vm.Tool == AnnotTool.AddText ? Cursors.IBeam : Cursors.Arrow,
            AnnotTool.EditObjects => ImageCursor(page, px, pt),
            _ => Cursors.Cross,
        };
    }

    private Cursor ImageCursor(PageViewModel page, Point px, PdfPoint pt)
    {
        if (_vm!.SelectedImage is { } sel && sel.PageIndex == page.Index)
        {
            int corner = CornerAt(sel.Box, px);
            if (corner >= 0) return corner is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }
        return _vm.HitTestImage(page.Index, pt.X, pt.Y) is not null ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void ContinueGesture(Point px, PageViewModel page)
    {
        switch (_gesture)
        {
            case Gesture.Text:
                var pt = ToPage(px);
                int focus = page.NearestWord(pt.X, pt.Y, double.MaxValue);
                if (focus >= 0)
                {
                    if (focus != _dragAnchor) _moved = true;
                    _vm!.SetSelection(page, _dragAnchor, focus);
                }
                break;

            case Gesture.Shape when _preview is not null:
                _moved = true;
                double x = Math.Min(px.X, _startPx.X), y = Math.Min(px.Y, _startPx.Y);
                if (_preview is Line line) { line.X2 = px.X; line.Y2 = px.Y; }
                else
                {
                    Canvas.SetLeft(_preview, x);
                    Canvas.SetTop(_preview, y);
                    _preview.Width = Math.Abs(px.X - _startPx.X);
                    _preview.Height = Math.Abs(px.Y - _startPx.Y);
                }
                break;

            case Gesture.Ink when _inkPreview is not null:
                _moved = true;
                if ((px - _ink[^1]).Length >= 1.5)
                {
                    _ink.Add(px);
                    _inkPreview.Points.Add(px);
                }
                break;

            case Gesture.MoveAnnotation when _preview is not null && _vm!.SelectedAnnotation is { } a:
                _moved = true;
                Canvas.SetLeft(_preview, a.Box.X0 * _vm.Zoom + px.X - _startPx.X);
                Canvas.SetTop(_preview, a.Box.Y0 * _vm.Zoom + px.Y - _startPx.Y);
                break;

            case Gesture.MoveImage when _preview is not null && _vm!.SelectedImage is { } im:
                _moved = true;
                Canvas.SetLeft(_preview, im.Box.X0 * _vm.Zoom + px.X - _startPx.X);
                Canvas.SetTop(_preview, im.Box.Y0 * _vm.Zoom + px.Y - _startPx.Y);
                break;

            case Gesture.ResizeImage when _preview is not null && _vm!.SelectedImage is { } img:
                _moved = true;
                _resizeRectPx = ResizedRect(img.Box, _vm.Zoom, px);
                Canvas.SetLeft(_preview, _resizeRectPx.X);
                Canvas.SetTop(_preview, _resizeRectPx.Y);
                _preview.Width = _resizeRectPx.Width;
                _preview.Height = _resizeRectPx.Height;
                break;
        }
    }

    private void OnPageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None || _gGrid is null || _gPage is null || _vm is null) return;
        var grid = _gGrid;
        var page = _gPage;
        var gesture = _gesture;
        var end = e.GetPosition(grid);
        bool moved = _moved;

        grid.ReleaseMouseCapture();
        DrawLayerOf(grid)?.Children.Clear();
        (_gesture, _gGrid, _gPage, _preview, _inkPreview) = (Gesture.None, null, null, null, null);
        var markupHit = _pendingMarkupHit;
        _pendingMarkupHit = null;

        switch (gesture)
        {
            case Gesture.Text:
                if (_vm.IsMarkupTool && moved) _vm.ApplyMarkupToSelection();
                else if (!moved && markupHit is not null)
                {
                    _vm.ClearSelection();
                    _vm.SelectAnnotation(markupHit);
                }
                break;

            case Gesture.Shape:
                FinishShape(page, ToPage(_startPx), ToPage(end));
                break;

            case Gesture.Ink:
                _vm.AddInkStroke(page, _ink.Select(ToPage).ToList());
                break;

            case Gesture.MoveAnnotation when moved:
                _vm.MoveSelectedAnnotation((end.X - _startPx.X) / _vm.Zoom, (end.Y - _startPx.Y) / _vm.Zoom);
                break;

            case Gesture.MoveImage when moved:
                _vm.MoveSelectedImage((end.X - _startPx.X) / _vm.Zoom, (end.Y - _startPx.Y) / _vm.Zoom);
                break;

            case Gesture.ResizeImage when moved:
                double zoom = _vm.Zoom;
                _vm.ResizeSelectedImage(new PdfRect(_resizeRectPx.Left / zoom, _resizeRectPx.Top / zoom,
                                                    _resizeRectPx.Right / zoom, _resizeRectPx.Bottom / zoom));
                break;
        }
    }

    private void FinishShape(PageViewModel page, PdfPoint a, PdfPoint b)
    {
        double dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        switch (_vm!.Tool)
        {
            case AnnotTool.FreeText:
                // Un clic sin arrastre crea un cuadro de tamaño por defecto.
                var box = Math.Max(dx, dy) < 8
                    ? new PdfRect(a.X, a.Y, a.X + DefaultTextWidth, a.Y + DefaultTextHeight)
                    : PdfRect.FromPoints(a.X, a.Y, b.X, b.Y);
                _vm.AddFreeTextIn(page, box);
                break;
            case AnnotTool.Line when Math.Max(dx, dy) >= 4:
                _vm.AddShape(page, AnnotationKind.Line, a, b);
                break;
            case AnnotTool.Arrow when Math.Max(dx, dy) >= 4:
                _vm.AddShape(page, AnnotationKind.Arrow, a, b);
                break;
            case AnnotTool.Rectangle when dx >= 4 && dy >= 4:
                _vm.AddShape(page, AnnotationKind.Rectangle, a, b);
                break;
            case AnnotTool.Ellipse when dx >= 4 && dy >= 4:
                _vm.AddShape(page, AnnotationKind.Ellipse, a, b);
                break;
        }
    }

    // ================= Imágenes: seleccionar, mover, redimensionar =================

    private const double HandleHitPx = 9;

    /// <summary>Esquina (0-3) de la caja bajo el puntero, o -1.</summary>
    private int CornerAt(PdfRect box, Point px)
    {
        double z = _vm!.Zoom;
        (double X, double Y)[] corners = [(box.X0, box.Y0), (box.X1, box.Y0), (box.X0, box.Y1), (box.X1, box.Y1)];
        for (int i = 0; i < corners.Length; i++)
            if (Math.Abs(px.X - corners[i].X * z) <= HandleHitPx && Math.Abs(px.Y - corners[i].Y * z) <= HandleHitPx) return i;
        return -1;
    }

    private void HandleImageClick(PageViewModel page, Grid grid, Point px, PdfPoint pt)
    {
        if (_vm!.SelectedImage is { } sel && sel.PageIndex == page.Index)
        {
            int corner = CornerAt(sel.Box, px);
            if (corner >= 0)
            {
                BeginGesture(Gesture.ResizeImage, page, grid, px);
                _resizeCorner = corner;
                return;
            }
        }

        var hit = _vm.HitTestImage(page.Index, pt.X, pt.Y);
        _vm.SelectImage(hit);
        if (hit is not null) BeginGesture(Gesture.MoveImage, page, grid, px);
    }

    /// <summary>Caja redimensionada arrastrando una esquina: la opuesta queda fija y se conserva la proporción.</summary>
    private Rect ResizedRect(PdfRect box, double z, Point px)
    {
        var anchor = _resizeCorner switch
        {
            0 => new Point(box.X1 * z, box.Y1 * z),
            1 => new Point(box.X0 * z, box.Y1 * z),
            2 => new Point(box.X1 * z, box.Y0 * z),
            _ => new Point(box.X0 * z, box.Y0 * z),
        };
        double width = Math.Max(8, Math.Abs(px.X - anchor.X));
        double height = width * box.Height / box.Width;
        double x = _resizeCorner is 0 or 2 ? anchor.X - width : anchor.X;
        double y = _resizeCorner is 0 or 1 ? anchor.Y - height : anchor.Y;
        return new Rect(x, y, width, height);
    }

    // ================= Editor de texto flotante =================

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Tool):
                CloseTextEditor(); // cambiar de herramienta descarta la edición abierta
                break;
            case nameof(DocumentViewModel.TextFamily) or nameof(DocumentViewModel.TextSize) or nameof(DocumentViewModel.TextBold)
                or nameof(DocumentViewModel.TextItalic) or nameof(DocumentViewModel.TextColor) or nameof(DocumentViewModel.TextAlignment):
                ApplyEditorStyle();
                break;
        }
    }

    private void ShowTextEditor(Grid grid, TextEditSession session)
    {
        CloseTextEditor();
        if (_vm is null) return;
        double z = _vm.Zoom;
        var box = session.Box;
        bool multiline = session.IsParagraph || session.IsNew;

        // Sin el estilo del tema: el tema oscuro pinta el cuadro de gris y los botones de blanco sobre la hoja blanca.
        var tb = new TextBox
        {
            Style = null,
            Text = session.Text,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Padding = new Thickness(0),
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            MinWidth = Math.Max(box.Width * z + 8, 110),
            MaxWidth = multiline ? Math.Max(box.Width * z + 8, 240) : double.PositiveInfinity,
            MaxHeight = 320,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        if (multiline) tb.MinHeight = Math.Max(box.Height * z, _vm.TextSize * z * 1.6);

        var apply = new Button { Style = null, Background = Brushes.WhiteSmoke, Foreground = Brushes.Black, Content = "✓ Aplicar", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 3, 4, 0), ToolTip = multiline ? "Ctrl+Intro" : "Intro" };
        var cancel = new Button { Style = null, Background = Brushes.WhiteSmoke, Foreground = Brushes.Black, Content = "✕ Cancelar", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 3, 0, 0), ToolTip = "Esc" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(tb);
        panel.Children.Add(buttons);

        _editorHost = new Border
        {
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(box.X0 * z - 1, box.Y0 * z - 1, 0, 0),
        };
        _session = session;
        _editorGrid = grid;
        _editBox = tb;
        grid.Children.Add(_editorHost);
        grid.Unloaded += OnEditorGridUnloaded;
        ApplyEditorStyle();

        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseTextEditor(); e.Handled = true; }
            else if (e.Key == Key.Enter && (!multiline || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CommitEditor(); e.Handled = true; }
        };
        apply.Click += (_, _) => CommitEditor();
        cancel.Click += (_, _) => CloseTextEditor();

        Dispatcher.BeginInvoke(() =>
        {
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>El cuadro imita el formato que se va a aplicar (fuente, tamaño, color, alineación).</summary>
    private void ApplyEditorStyle()
    {
        if (_editBox is null || _vm is null) return;
        _editBox.FontFamily = new FontFamily(_vm.TextFamily);
        _editBox.FontSize = Math.Max(4, _vm.TextSize * _vm.Zoom);
        _editBox.FontWeight = _vm.TextBold ? FontWeights.Bold : FontWeights.Normal;
        _editBox.FontStyle = _vm.TextItalic ? FontStyles.Italic : FontStyles.Normal;
        _editBox.Foreground = _vm.TextColor?.Brush ?? Brushes.Black;
        _editBox.TextAlignment = _vm.TextAlignment switch
        {
            DocsDR.Core.TextAlign.Center => TextAlignment.Center,
            DocsDR.Core.TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    private bool IsInsideEditor(object? source)
    {
        for (var d = source as DependencyObject; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, _editorHost)) return true;
        return false;
    }

    private void CommitEditor()
    {
        if (_session is not { } session || _editBox is null || _vm is null) return;
        var text = _editBox.Text;
        CloseTextEditor();
        // Sin cambios no se toca el documento: reescribir el renglón perdería su formato mezclado (negritas, colores).
        if (!_vm.TextEditChanged(session, text))
        {
            _vm.SearchStatus = "Sin cambios";
            return;
        }
        _vm.CommitTextEdit(session, text);
    }

    /// <summary>Un clic fuera del editor lo aplica solo si hay algo distinto; si no, simplemente se cierra.</summary>
    private void CommitEditorIfChanged()
    {
        if (_session is { } session && _editBox is not null && _vm is not null && _vm.TextEditChanged(session, _editBox.Text)) CommitEditor();
        else CloseTextEditor();
    }

    private void OnEditorGridUnloaded(object sender, RoutedEventArgs e) => CloseTextEditor();

    private void CloseTextEditor()
    {
        if (_editorGrid is not null)
        {
            _editorGrid.Children.Remove(_editorHost);
            _editorGrid.Unloaded -= OnEditorGridUnloaded;
        }
        (_session, _editorGrid, _editorHost, _editBox) = (null, null, null, null);
    }

    // ================= Teclado =================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_vm is null) return;
        // No robamos teclas de los cuadros de texto (búsqueda, número de página).
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Escape)
        {
            CancelGesture();
            _vm.Tool = AnnotTool.Select;
            _vm.ClearSelection();
            _vm.SelectAnnotation(null);
            _vm.SelectImage(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.DeleteSelectedImageCommand.CanExecute(null))
        {
            _vm.DeleteSelectedImageCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.DeleteSelectedAnnotationCommand.CanExecute(null))
        {
            _vm.DeleteSelectedAnnotationCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && _vm.HasSelection)
        {
            CopySelection();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A && _vm.Pages.Count > 0)
        {
            _vm.SelectAll(_vm.Pages[Math.Clamp(_vm.CurrentPage - 1, 0, _vm.Pages.Count - 1)]);
            e.Handled = true;
        }
    }

    private void CancelGesture()
    {
        if (_gGrid is null) return;
        _gGrid.ReleaseMouseCapture();
        DrawLayerOf(_gGrid)?.Children.Clear();
        (_gesture, _gGrid, _gPage, _preview, _inkPreview) = (Gesture.None, null, null, null, null);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelection();

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var page = (sender as FrameworkElement)?.DataContext as PageViewModel
                   ?? _vm.Pages[Math.Clamp(_vm.CurrentPage - 1, 0, _vm.Pages.Count - 1)];
        _vm.SelectAll(page);
    }

    private void CopySelection()
    {
        if (_vm is null || !_vm.CopySelection()) return;
        if (Application.Current.MainWindow?.DataContext is MainViewModel main)
            main.StatusText = _vm.SearchStatus;
    }
}

public sealed class LevelToIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(((value as int? ?? 1) - 1) * 14, 2, 0, 2);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True cuando el valor enlazado es igual al parámetro (para marcar la herramienta activa).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
