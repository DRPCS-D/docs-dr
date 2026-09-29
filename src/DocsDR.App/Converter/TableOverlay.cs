using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DocsDR.Core;

namespace DocsDR.App.Converter;

/// <summary>
/// Capa sobre la página renderizada donde se dibujan las tablas y se editan a mano:
/// dibujar región, mover/redimensionar, agregar/mover/quitar separadores de columna y fila.
/// Trabaja en coordenadas de página (puntos) escaladas por <see cref="ConverterViewModel.PageZoom"/>.
/// </summary>
public sealed class TableOverlay : FrameworkElement
{
    private enum Drag { None, Draw, Move, Left, Right, Top, Bottom, Column, Row }

    private static readonly Brush SelectedStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xD9)));
    private static readonly Brush OtherStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)));
    private static readonly Brush SelectedFill = Freeze(new SolidColorBrush(Color.FromArgb(28, 0x1E, 0x6F, 0xD9)));
    private static readonly Brush OtherFill = Freeze(new SolidColorBrush(Color.FromArgb(22, 0xE6, 0x7E, 0x22)));
    private static readonly Pen DrawPen = Freeze(new Pen(Brushes.DodgerBlue, 1.5) { DashStyle = DashStyles.Dash });

    private Drag _drag;
    private int _dragIndex;
    private Point _dragStart;
    private PdfRect _origRegion;
    private List<double> _origCols = [], _origRows = [];
    private Point _drawCurrent;
    private bool _modified;

    public TableOverlay()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    public ConverterViewModel? ViewModel { get; set; }

    private static double Z => ConverterViewModel.PageZoom;
    private const double HitPx = 5;

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // superficie para el mouse
        if (ViewModel is null) return;

        int n = 0;
        foreach (var t in ViewModel.CurrentTables)
        {
            n++;
            bool sel = ReferenceEquals(t, ViewModel.SelectedTable);
            var stroke = sel ? SelectedStroke : OtherStroke;
            var rect = ToPx(t.Region);
            dc.DrawRectangle(sel ? SelectedFill : OtherFill, new Pen(stroke, sel ? 2 : 1.5), rect);

            var sepPen = new Pen(stroke, sel ? 1.5 : 1);
            foreach (var x in t.ColumnSeparators)
                dc.DrawLine(sepPen, new Point(x * Z, rect.Top), new Point(x * Z, rect.Bottom));
            var rowPen = new Pen(stroke, 0.8) { DashStyle = DashStyles.Dash };
            foreach (var y in t.RowSeparators)
                dc.DrawLine(rowPen, new Point(rect.Left, y * Z), new Point(rect.Right, y * Z));

            var method = t.Method switch { DetectionMethod.Lattice => "con bordes", DetectionMethod.Stream => "sin bordes", _ => "manual" };
            var label = new FormattedText($" Tabla {n} · {method} · {t.ColumnCount} col ", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double ly = Math.Max(0, rect.Top - label.Height - 1);
            dc.DrawRectangle(stroke, null, new Rect(rect.Left, ly, label.Width, label.Height));
            dc.DrawText(label, new Point(rect.Left, ly));

            if (sel)
            {
                foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight })
                    dc.DrawRectangle(Brushes.White, new Pen(stroke, 1.5), new Rect(corner.X - 4, corner.Y - 4, 8, 8));
            }
        }

        if (_drag == Drag.Draw)
            dc.DrawRectangle(null, DrawPen, new Rect(_dragStart, _drawCurrent));
    }

    private static Rect ToPx(PdfRect r) => new(r.X0 * Z, r.Y0 * Z, r.Width * Z, r.Height * Z);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ViewModel is null) return;
        Focus();
        var p = e.GetPosition(this);
        double px = p.X / Z, py = p.Y / Z;

        switch (ViewModel.Mode)
        {
            case OverlayMode.DrawTable:
                BeginDrag(Drag.Draw, p, null);
                _drawCurrent = p;
                break;

            case OverlayMode.AddColumn:
                if (TableAt(px, py) is { } tc) ViewModel.AddColumnAt(tc, px);
                break;

            case OverlayMode.AddRow:
                if (TableAt(px, py) is { } tr) ViewModel.AddRowAt(tr, py);
                break;

            default:
                var sel = ViewModel.SelectedTable;
                var (kind, index) = sel is null ? (Drag.None, -1) : HitTest(sel, p);
                if (kind != Drag.None)
                {
                    BeginDrag(kind, p, sel);
                    _dragIndex = index;
                }
                else
                {
                    var hit = TableAt(px, py);
                    ViewModel.SelectTable(hit);
                    if (hit is not null) BeginDrag(Drag.Move, p, hit);
                }
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private void BeginDrag(Drag kind, Point p, TableDefinition? t)
    {
        _drag = kind;
        _dragStart = p;
        _modified = false;
        if (t is not null)
        {
            _origRegion = t.Region;
            _origCols = [.. t.ColumnSeparators];
            _origRows = [.. t.RowSeparators];
        }
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ViewModel is null) return;
        var p = e.GetPosition(this);

        if (_drag == Drag.None)
        {
            UpdateCursor(p);
            return;
        }
        if (_drag == Drag.Draw)
        {
            _drawCurrent = p;
            InvalidateVisual();
            return;
        }

        var t = ViewModel.SelectedTable;
        if (t is null) return;
        double dx = (p.X - _dragStart.X) / Z, dy = (p.Y - _dragStart.Y) / Z;
        if (!_modified && Math.Abs(dx) < 1 && Math.Abs(dy) < 1) return;
        _modified = true;
        var r = _origRegion;
        const double min = 10;

        switch (_drag)
        {
            case Drag.Move:
                t.Region = new PdfRect(r.X0 + dx, r.Y0 + dy, r.X1 + dx, r.Y1 + dy);
                t.ColumnSeparators = _origCols.Select(x => x + dx).ToList();
                t.RowSeparators = _origRows.Select(y => y + dy).ToList();
                break;
            case Drag.Left: t.Region = r with { X0 = Math.Min(r.X0 + dx, r.X1 - min) }; break;
            case Drag.Right: t.Region = r with { X1 = Math.Max(r.X1 + dx, r.X0 + min) }; break;
            case Drag.Top: t.Region = r with { Y0 = Math.Min(r.Y0 + dy, r.Y1 - min) }; break;
            case Drag.Bottom: t.Region = r with { Y1 = Math.Max(r.Y1 + dy, r.Y0 + min) }; break;
            case Drag.Column:
                t.ColumnSeparators[_dragIndex] = Math.Clamp(_origCols[_dragIndex] + dx, r.X0 + 1, r.X1 - 1);
                break;
            case Drag.Row:
                t.RowSeparators[_dragIndex] = Math.Clamp(_origRows[_dragIndex] + dy, r.Y0 + 1, r.Y1 - 1);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (ViewModel is null || _drag == Drag.None) return;
        ReleaseMouseCapture();
        var kind = _drag;
        _drag = Drag.None;

        if (kind == Drag.Draw)
        {
            var p = e.GetPosition(this);
            var region = PdfRect.FromPoints(_dragStart.X / Z, _dragStart.Y / Z, p.X / Z, p.Y / Z);
            if (region.Width > 10 && region.Height > 10) ViewModel.OnRegionDrawn(region);
        }
        else if (_modified && ViewModel.SelectedTable is { } t)
        {
            ViewModel.NotifyEdited(t);
        }
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (ViewModel?.SelectedTable is not { } t) return;
        var (kind, index) = HitTest(t, e.GetPosition(this));
        if (kind is Drag.Column or Drag.Row)
        {
            ViewModel.RemoveSeparator(t, kind == Drag.Column, index);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && ViewModel?.DeleteSelectedCommand.CanExecute(null) == true)
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ViewModel is not null)
        {
            ViewModel.Mode = OverlayMode.Select;
        }
    }

    private void UpdateCursor(Point p)
    {
        if (ViewModel is null) return;
        Cursor = ViewModel.Mode switch
        {
            OverlayMode.DrawTable => Cursors.Cross,
            OverlayMode.AddColumn or OverlayMode.AddRow => Cursors.Hand,
            _ => ViewModel.SelectedTable is { } t ? HitTest(t, p).Kind switch
            {
                Drag.Column or Drag.Left or Drag.Right => Cursors.SizeWE,
                Drag.Row or Drag.Top or Drag.Bottom => Cursors.SizeNS,
                _ => t.Region.Contains(p.X / Z, p.Y / Z) ? Cursors.SizeAll : Cursors.Arrow,
            } : Cursors.Arrow,
        };
    }

    /// <summary>Qué parte de la tabla seleccionada está bajo el puntero (separadores tienen prioridad sobre bordes).</summary>
    private static (Drag Kind, int Index) HitTest(TableDefinition t, Point p)
    {
        var r = ToPx(t.Region);
        bool insideX = p.X >= r.Left - HitPx && p.X <= r.Right + HitPx;
        bool insideY = p.Y >= r.Top - HitPx && p.Y <= r.Bottom + HitPx;
        if (!insideX || !insideY) return (Drag.None, -1);

        for (int i = 0; i < t.ColumnSeparators.Count; i++)
            if (Math.Abs(p.X - t.ColumnSeparators[i] * Z) <= HitPx) return (Drag.Column, i);
        for (int i = 0; i < t.RowSeparators.Count; i++)
            if (Math.Abs(p.Y - t.RowSeparators[i] * Z) <= HitPx) return (Drag.Row, i);

        if (Math.Abs(p.X - r.Left) <= HitPx) return (Drag.Left, -1);
        if (Math.Abs(p.X - r.Right) <= HitPx) return (Drag.Right, -1);
        if (Math.Abs(p.Y - r.Top) <= HitPx) return (Drag.Top, -1);
        if (Math.Abs(p.Y - r.Bottom) <= HitPx) return (Drag.Bottom, -1);
        return (Drag.None, -1);
    }

    private TableDefinition? TableAt(double x, double y) =>
        ViewModel?.CurrentTables.Where(t => t.Region.Contains(x, y))
            .OrderBy(t => t.Region.Width * t.Region.Height) // la más pequeña si se superponen
            .FirstOrDefault();
}
