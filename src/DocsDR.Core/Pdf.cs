namespace DocsDR.Core;

/// <summary>Bitmap RGB24 de una página renderizada.</summary>
public sealed record RenderedPage(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>Bitmap en escala de grises (1 byte por píxel) usado para detectar líneas en páginas escaneadas.</summary>
public sealed record GrayImage(int Width, int Height, byte[] Pixels, double Scale);

public sealed record SearchHit(int PageIndex, PdfRect Box);

public sealed record OutlineItem(int Level, string Title, int PageIndex);

/// <summary>
/// Contenido de una página listo para detección de tablas.
/// En páginas escaneadas, <see cref="Lines"/> viene vacío y <see cref="ScanImage"/> trae la imagen
/// para que el detector busque las líneas de la tabla en píxeles.
/// </summary>
public sealed record PageContent(
    int PageIndex,
    double Width,
    double Height,
    IReadOnlyList<TextWord> Words,
    IReadOnlyList<LineSegment> Lines,
    bool IsScanned,
    GrayImage? ScanImage = null,
    bool OcrUnavailable = false);

public interface IPdfDocument : IDisposable
{
    string FilePath { get; }
    int PageCount { get; }
    (double Width, double Height) GetPageSize(int pageIndex);
    RenderedPage Render(int pageIndex, double zoom);
    IReadOnlyList<TextWord> GetWords(int pageIndex);
    IReadOnlyList<SearchHit> Search(string text);
    IReadOnlyList<OutlineItem> GetOutline();

    /// <summary>Palabras y líneas de la página. Si la página es escaneada y hay datos de OCR disponibles, aplica OCR.</summary>
    PageContent GetPageContent(int pageIndex, bool allowOcr = true);
}

public interface IPdfService
{
    IPdfDocument Open(string path, string? password = null);

    /// <summary>Une los PDF indicados, en ese orden, en un archivo nuevo.</summary>
    void Merge(IReadOnlyList<string> files, string destPath);
}

public sealed class PdfPasswordRequiredException(string path) : Exception($"El documento '{path}' requiere contraseña.");
