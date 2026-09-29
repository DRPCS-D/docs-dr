using DocsDR.Core;
using MuPDF.NET;

namespace DocsDR.Pdf;

public sealed class MuPdfService : IPdfService
{
    public IPdfDocument Open(string path, string? password = null) => new MuPdfDocument(path, password);

    public void Merge(IReadOnlyList<string> files, string destPath) => MuPdfDocument.MergeFiles(files, destPath);

    public void ImagesToPdf(IReadOnlyList<string> images, string destPath) => MuPdfDocument.ImagesToPdfFile(images, destPath);
}

/// <summary>Adaptador de MuPDF.NET. Todo acceso nativo pasa por <see cref="NativeLock"/> (MuPDF no es thread-safe).</summary>
public sealed partial class MuPdfDocument : IPdfDocument
{
    /// <summary>Candado global: el contexto nativo de MuPDF es compartido entre documentos.</summary>
    public static readonly object NativeLock = new();

    private const double ScanDetectionDpi = 150;
    private const double OcrDpi = 300;

    private Document _doc;
    private readonly string? _password;
    private readonly Dictionary<int, PageContent> _contentCache = [];

    /// <summary>
    /// El archivo se carga completo en memoria: así no queda bloqueado en disco y se puede
    /// guardar encima del original sin conflictos.
    /// </summary>
    public MuPdfDocument(string path, string? password = null)
    {
        FilePath = path;
        _password = password;
        var bytes = File.ReadAllBytes(path);
        lock (NativeLock)
        {
            try { _doc = OpenBytes(bytes, password); }
            catch (PdfPasswordRequiredException) { throw new PdfPasswordRequiredException(path); }
        }
    }

    /// <summary>
    /// Abre un PDF desde memoria. Si está protegido, exige <paramref name="password"/> y lo verifica
    /// (<c>NeedsPass</c> sigue en true después de autenticar, así que no se vuelve a consultar).
    /// </summary>
    internal static Document OpenBytes(byte[] bytes, string? password)
    {
        var doc = new Document(stream: bytes, fileType: "pdf");
        if (doc.NeedsPass && (password is null || doc.Authenticate(password) == 0))
        {
            doc.Close();
            throw new PdfPasswordRequiredException("");
        }
        return doc;
    }

    public string FilePath { get; private set; }
    public int PageCount { get { lock (NativeLock) { return _doc.PageCount; } } }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (NativeLock)
        {
            var r = _doc[pageIndex].Rect;
            return (r.Width, r.Height);
        }
    }

    public RenderedPage Render(int pageIndex, double zoom)
    {
        lock (NativeLock)
        {
            var page = _doc[pageIndex];
            var pix = page.GetPixmap(matrix: new Matrix((float)zoom, (float)zoom), alpha: false);
            return new RenderedPage(pix.Width, pix.Height, pix.Stride, pix.Samples);
        }
    }

    public IReadOnlyList<TextWord> GetWords(int pageIndex)
    {
        lock (NativeLock)
        {
            var page = _doc[pageIndex];
            return ToWords(page.GetTextWords(sort: true), page);
        }
    }

    public IReadOnlyList<SearchHit> Search(string text)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        lock (NativeLock)
        {
            for (int i = 0; i < PageCount; i++)
            {
                foreach (var q in _doc[i].SearchFor(text))
                {
                    hits.Add(new SearchHit(i, ToView(_doc[i], q.Rect)));
                }
            }
        }
        return hits;
    }

    public IReadOnlyList<OutlineItem> GetOutline()
    {
        var items = new List<OutlineItem>();
        lock (NativeLock)
        {
            foreach (var t in _doc.GetToc(simple: true))
                items.Add(new OutlineItem(t.Item1, t.Item2, Math.Max(0, t.Item3 - 1)));
        }
        return items;
    }

    public PageContent GetPageContent(int pageIndex, bool allowOcr = true)
    {
        lock (NativeLock)
        {
            if (_contentCache.TryGetValue(pageIndex, out var cached) && (cached.Words.Count > 0 || !allowOcr))
                return cached;

            var page = _doc[pageIndex];
            var rect = page.Rect;
            var words = ToWords(page.GetTextWords(sort: true), page);
            bool scanned = IsScanned(page, words);

            PageContent content;
            if (!scanned)
            {
                content = new PageContent(pageIndex, rect.Width, rect.Height, words, ExtractLines(page), false);
            }
            else
            {
                double scale = ScanDetectionDpi / 72.0;
                var gray = page.GetPixmap(matrix: new Matrix((float)scale, (float)scale), cs: Colorspace.Gray, alpha: false);
                var image = new GrayImage(gray.Width, gray.Height, CompactRows(gray.Samples, gray.Width, gray.Height, gray.Stride), scale);

                bool ocrUnavailable = false;
                if (allowOcr && words.Count == 0)
                {
                    var tess = TessdataLocator.Find();
                    if (tess is null)
                        ocrUnavailable = true;
                    else
                    {
                        var tp = page.GetTextPageOcr(flags: 0, language: tess.Value.Languages, dpi: (int)OcrDpi, full: true, tessdata: tess.Value.Path);
                        words = ToWords(page.GetTextWords(textpage: tp, sort: true), page);
                    }
                }
                content = new PageContent(pageIndex, rect.Width, rect.Height, words, [], true, image, ocrUnavailable);
            }

            _contentCache[pageIndex] = content;
            return content;
        }
    }

    private static bool IsScanned(Page page, IReadOnlyList<TextWord> words)
    {
        int chars = words.Sum(w => w.Text.Length);
        if (chars >= 20) return false;
        var area = page.Rect.Width * page.Rect.Height;
        double imageArea = 0;
        foreach (var img in page.GetImageInfo())
        {
            var b = img.Bbox;
            imageArea += Math.Max(0, b.Width) * Math.Max(0, b.Height);
        }
        return imageArea > area * 0.3;
    }

    private static List<TextWord> ToWords(List<(float x0, float y0, float x1, float y1, string word, int blockNo, int lineNo, int wordNo)> blocks, Page page) =>
        blocks.Where(b => !string.IsNullOrWhiteSpace(b.word))
              .Select(b => new TextWord(b.word.Trim(), ToView(page, new MuPDF.NET.Rect(b.x0, b.y0, b.x1, b.y1))))
              .ToList();

    // ---- Páginas giradas ----
    // MuPDF devuelve el texto, las búsquedas, las imágenes y los trazos en el espacio SIN girar de la página, pero
    // renderiza (y crea anotaciones) con la página girada. Toda la app trabaja en coordenadas de la VISTA: se convierte aquí.

    /// <summary>Rectángulo del espacio sin girar al de la vista.</summary>
    internal static PdfRect ToView(Page page, MuPDF.NET.Rect r) =>
        page.Rotation == 0 ? FromRect(r) : FromRect(r * page.RotationMatrix);

    /// <summary>Rectángulo de la vista al espacio sin girar (el que usan las funciones de escritura de MuPDF).</summary>
    internal static MuPDF.NET.Rect ToUnrotated(Page page, PdfRect r) =>
        page.Rotation == 0 ? ToRect(r) : ToRect(r) * page.DerotationMatrix;

    internal static MuPDF.NET.Point ToUnrotated(Page page, double x, double y)
    {
        var p = new MuPDF.NET.Point((float)x, (float)y);
        return page.Rotation == 0 ? p : p * page.DerotationMatrix;
    }

    /// <summary>Punto del espacio sin girar al de la vista.</summary>
    internal static (double X, double Y) ToViewPoint(Page page, MuPDF.NET.Point p)
    {
        var q = page.Rotation == 0 ? p : p * page.RotationMatrix;
        return (q.X, q.Y);
    }

    /// <summary>Desplazamiento de la vista expresado en el espacio sin girar.</summary>
    internal static (double X, double Y) ToUnrotatedVector(Page page, double dx, double dy)
    {
        if (page.Rotation == 0) return (dx, dy);
        var m = page.DerotationMatrix;
        return (m.A * dx + m.C * dy, m.B * dx + m.D * dy);
    }

    /// <summary>Dirección (vector unitario) de un texto tal como se ve en la vista.</summary>
    internal static (double X, double Y) ToViewDirection(Page page, double dx, double dy)
    {
        if (page.Rotation == 0) return (dx, dy);
        var m = page.RotationMatrix;
        return (m.A * dx + m.C * dy, m.B * dx + m.D * dy);
    }

    /// <summary>Convierte los trazos vectoriales de la página en segmentos horizontales/verticales.</summary>
    private static List<LineSegment> ExtractLines(Page page)
    {
        var result = new List<LineSegment>();
        var toView = page.Rotation == 0 ? null : (Matrix?)page.RotationMatrix;
        foreach (var path in page.GetDrawings())
        {
            if (path.Items is null) continue;
            foreach (var item in path.Items)
            {
                switch (item.Type)
                {
                    case "l" when item.P1 is not null:
                        // MuPDF.NET guarda el punto final de la línea en LastPoint (P2 queda vacío).
                        var end = item.P2 ?? item.LastPoint;
                        if (end is not null)
                        {
                            var a = toView is { } m ? item.P1 * m : item.P1;
                            var b = toView is { } m2 ? end * m2 : end;
                            AddIfAxisAligned(result, a.X, a.Y, b.X, b.Y);
                        }
                        break;
                    case "re" when item.Rect is not null:
                        AddRect(result, toView is { } m3 ? item.Rect * m3 : item.Rect);
                        break;
                    case "qu" when item.Quad is not null && item.Quad.IsRectangular:
                        AddRect(result, toView is { } m4 ? item.Quad.Rect * m4 : item.Quad.Rect);
                        break;
                }
            }
        }
        return result;
    }

    private static void AddRect(List<LineSegment> result, MuPDF.NET.Rect r)
    {
        const double thin = 3.0;
        if (r.Height <= thin && r.Width > thin)
        {
            // Rectángulo delgado relleno usado como línea horizontal.
            double y = (r.Y0 + r.Y1) / 2;
            result.Add(new LineSegment(r.X0, y, r.X1, y));
        }
        else if (r.Width <= thin && r.Height > thin)
        {
            double x = (r.X0 + r.X1) / 2;
            result.Add(new LineSegment(x, r.Y0, x, r.Y1));
        }
        else if (r.Width > thin && r.Height > thin)
        {
            result.Add(new LineSegment(r.X0, r.Y0, r.X1, r.Y0));
            result.Add(new LineSegment(r.X0, r.Y1, r.X1, r.Y1));
            result.Add(new LineSegment(r.X0, r.Y0, r.X0, r.Y1));
            result.Add(new LineSegment(r.X1, r.Y0, r.X1, r.Y1));
        }
    }

    private static void AddIfAxisAligned(List<LineSegment> result, double x0, double y0, double x1, double y1)
    {
        var seg = new LineSegment(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        if (seg.IsHorizontal || seg.IsVertical) result.Add(seg);
    }

    private static byte[] CompactRows(byte[] samples, int width, int height, int stride)
    {
        if (stride == width) return samples;
        var dst = new byte[width * height];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(samples, y * stride, dst, y * width, width);
        return dst;
    }

    public void Dispose()
    {
        lock (NativeLock) { _doc.Close(); }
    }
}
