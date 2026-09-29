using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DocsDR.App.Services;
using DocsDR.Core;

namespace DocsDR.App.ViewModels;

public sealed record HighlightBox(double Left, double Top, double Width, double Height);

/// <summary>
/// Página del visor. La imagen se renderiza de forma perezosa la primera vez que la vista la pide
/// (con virtualización, solo las páginas visibles), y se vuelve a renderizar al cambiar el zoom.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    public const double ThumbnailZoom = 0.2;
    public const double HandleSize = 9;

    private readonly DocumentViewModel _owner;
    private ImageSource? _image;
    private double _imageZoom;
    private bool _rendering;
    private int _renderVersion;
    private ImageSource? _thumbnail;
    private bool _thumbRendering;
    private List<PdfRect> _hits = [];

    public PageViewModel(DocumentViewModel owner, int index, double width, double height)
    {
        _owner = owner;
        Index = index;
        PageWidth = width;
        PageHeight = height;
    }

    public int Index { get; }
    public int Number => Index + 1;
    public double PageWidth { get; }
    public double PageHeight { get; }
    public double DisplayWidth => PageWidth * _owner.Zoom;
    public double DisplayHeight => PageHeight * _owner.Zoom;
    public double ThumbWidth => PageWidth * ThumbnailZoom;
    public double ThumbHeight => PageHeight * ThumbnailZoom;

    public ObservableCollection<HighlightBox> Highlights { get; } = [];
    public ObservableCollection<HighlightBox> Selection { get; } = [];
    public ObservableCollection<HighlightBox> AnnotationSelection { get; } = [];
    public ObservableCollection<HighlightBox> Handles { get; } = [];
    private PdfRect? _annotationBox;
    private bool _showHandles;

    private IReadOnlyList<TextWord>? _words;
    private List<PdfRect> _selectionRects = [];

    /// <summary>Palabras de la página en orden de lectura (se cargan la primera vez que se necesitan).</summary>
    public IReadOnlyList<TextWord> Words => _words ??= _owner.Document.GetWords(Index);

    /// <summary>
    /// Índice de la palabra más cercana al punto (coordenadas de página, en puntos).
    /// Devuelve -1 si la más cercana está a más de <paramref name="maxDistance"/>.
    /// </summary>
    public int NearestWord(double x, double y, double maxDistance)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        var words = Words;
        for (int i = 0; i < words.Count; i++)
        {
            var b = words[i].Box;
            double dx = Math.Max(Math.Max(b.X0 - x, x - b.X1), 0);
            double dy = Math.Max(Math.Max(b.Y0 - y, y - b.Y1), 0);
            // La distancia vertical pesa más: preferimos quedarnos en el mismo renglón.
            double d = Math.Sqrt(dx * dx + 4 * dy * dy);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return bestDist <= maxDistance ? best : -1;
    }

    /// <summary>Muestra el resaltado de selección: un rectángulo por renglón.</summary>
    internal void SetSelectionRects(IEnumerable<PdfRect> rects)
    {
        _selectionRects = rects.ToList();
        UpdateSelection();
    }

    /// <summary>Contorno de la anotación seleccionada en esta página (null = ninguna).</summary>
    internal void SetAnnotationBox(PdfRect? box, bool handles = false)
    {
        _annotationBox = box;
        _showHandles = handles;
        UpdateAnnotationBox();
    }

    private void UpdateAnnotationBox()
    {
        AnnotationSelection.Clear();
        Handles.Clear();
        if (_annotationBox is not { } b) return;
        double z = _owner.Zoom;
        const double pad = 2;
        AnnotationSelection.Add(new HighlightBox(b.X0 * z - pad, b.Y0 * z - pad, b.Width * z + 2 * pad, b.Height * z + 2 * pad));
        if (!_showHandles) return;
        // Cuatro asas cuadradas en las esquinas (para redimensionar imágenes).
        const double h = HandleSize;
        foreach (var (cx, cy) in new[] { (b.X0, b.Y0), (b.X1, b.Y0), (b.X0, b.Y1), (b.X1, b.Y1) })
            Handles.Add(new HighlightBox(cx * z - h / 2, cy * z - h / 2, h, h));
    }

    private void UpdateSelection()
    {
        Selection.Clear();
        double z = _owner.Zoom;
        foreach (var r in _selectionRects)
            Selection.Add(new HighlightBox(r.X0 * z, r.Y0 * z, r.Width * z, r.Height * z));
    }

    public ImageSource? Image
    {
        get
        {
            if ((_image is null || _imageZoom != _owner.Zoom) && !_rendering) _ = RenderAsync();
            return _image;
        }
    }

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbRendering) _ = RenderThumbAsync();
            return _thumbnail;
        }
    }

    private async Task RenderAsync()
    {
        _rendering = true;
        int version = _renderVersion;
        try
        {
            double zoom = _owner.Zoom;
            var bmp = await PageImages.RenderAsync(_owner.Document, Index, zoom);
            if (version != _renderVersion) return; // el contenido cambió mientras se renderizaba
            _image = bmp;
            _imageZoom = zoom;
            _owner.TrackRendered(this);
            OnPropertyChanged(nameof(Image));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Error al renderizar la página {Page}", Number);
        }
        finally
        {
            _rendering = false;
            // Si hubo un cambio durante el render, se vuelve a pedir la imagen.
            if (version != _renderVersion) OnPropertyChanged(nameof(Image));
        }
    }

    /// <summary>
    /// El contenido de la página cambió (anotación, etc.): se vuelve a renderizar conservando la imagen
    /// anterior en pantalla hasta que llegue la nueva, para que no parpadee.
    /// </summary>
    internal void Refresh()
    {
        _renderVersion++;
        _imageZoom = -1;
        _words = null;
        _thumbnail = null;
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(Thumbnail));
    }

    private async Task RenderThumbAsync()
    {
        _thumbRendering = true;
        int version = _renderVersion;
        try
        {
            var bmp = await PageImages.RenderAsync(_owner.Document, Index, ThumbnailZoom);
            if (version != _renderVersion) return;
            _thumbnail = bmp;
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Error al renderizar la miniatura {Page}", Number);
        }
        finally
        {
            _thumbRendering = false;
            if (version != _renderVersion) OnPropertyChanged(nameof(Thumbnail));
        }
    }

    /// <summary>Libera el bitmap para ahorrar memoria (se regenera si vuelve a ser visible).</summary>
    internal void Evict() => _image = null;

    internal void OnZoomChanged()
    {
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
        OnPropertyChanged(nameof(Image));
        UpdateHighlights();
        UpdateSelection();
        UpdateAnnotationBox();
    }

    internal void SetHits(IEnumerable<PdfRect> hits)
    {
        _hits = hits.ToList();
        UpdateHighlights();
    }

    private void UpdateHighlights()
    {
        Highlights.Clear();
        double z = _owner.Zoom;
        foreach (var h in _hits)
            Highlights.Add(new HighlightBox(h.X0 * z, h.Y0 * z, h.Width * z, h.Height * z));
    }
}
