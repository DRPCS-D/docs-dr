namespace DocsDR.Core;

public readonly record struct PdfPoint(double X, double Y);

public readonly record struct PdfColor(byte R, byte G, byte B)
{
    public static PdfColor Yellow => new(255, 230, 0);
    public static PdfColor Green => new(80, 200, 90);
    public static PdfColor Blue => new(50, 130, 230);
    public static PdfColor Pink => new(255, 120, 190);
    public static PdfColor Orange => new(255, 150, 30);
    public static PdfColor Red => new(220, 40, 40);
    public static PdfColor Black => new(0, 0, 0);
}

public enum AnnotationKind
{
    Highlight, Underline, StrikeOut, Note, FreeText, Ink, Rectangle, Ellipse, Line, Arrow, Stamp, Other,
}

/// <summary>Anotación existente en una página. <see cref="Id"/> identifica la anotación dentro del documento.</summary>
public sealed record AnnotationInfo(
    int PageIndex, int Id, AnnotationKind Kind, string Content, string Author, PdfRect Box, PdfColor? Color)
{
    /// <summary>Resaltado/subrayado/tachado: van pegados al texto y no se mueven.</summary>
    public bool IsMarkup => Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut;

    /// <summary>Tipos cuyo rectángulo se puede desplazar sin rehacer su geometría interna.</summary>
    public bool CanMove => Kind is AnnotationKind.Note or AnnotationKind.FreeText or AnnotationKind.Stamp
        or AnnotationKind.Rectangle or AnnotationKind.Ellipse;
}

public enum TextAlign { Left, Center, Right }

/// <summary>Formato de un texto: familia (Arial, Times New Roman, Courier New), tamaño en puntos, color y estilo.</summary>
public sealed record TextFormat(string Family, double Size, PdfColor Color, bool Bold, bool Italic)
{
    public const string Sans = "Arial", Serif = "Times New Roman", Mono = "Courier New";
    public static IReadOnlyList<string> Families { get; } = [Sans, Serif, Mono];
}

/// <summary>Un renglón de texto del documento con su formato dominante.</summary>
public sealed record TextLineInfo(PdfRect Box, string Text, TextFormat Format);

/// <summary>Un bloque (normalmente un párrafo o una celda) formado por renglones consecutivos.</summary>
public sealed record TextBlockInfo(int PageIndex, PdfRect Box, IReadOnlyList<TextLineInfo> Lines)
{
    public TextFormat Format => Lines[0].Format;

    /// <summary>Texto del párrafo: los renglones se unen con espacios para poder reajustarlo al editar.</summary>
    public string ParagraphText => string.Join(" ", Lines.Select(l => l.Text));
}

/// <summary>Imagen colocada en una página. <see cref="Xref"/> es el objeto de imagen (puede repetirse en varias posiciones).</summary>
public sealed record PageImageInfo(int PageIndex, int Xref, PdfRect Box);

/// <summary>
/// Operaciones de modificación de un PDF. Todas las páginas se indican con índice base 0.
/// La app protege cada operación con una instantánea (<see cref="CreateSnapshot"/>) para poder deshacer.
/// </summary>
public interface IPdfEditor
{
    // ---- Anotaciones ----
    int AddTextMarkup(int page, AnnotationKind kind, IReadOnlyList<PdfRect> lines, PdfColor color, string author);
    int AddNote(int page, PdfPoint at, string text, PdfColor color, string author);
    int AddFreeText(int page, PdfRect box, string text, double fontSize, PdfColor color, string author);
    int AddInk(int page, IReadOnlyList<PdfPoint> stroke, PdfColor color, double width, string author);
    int AddShape(int page, AnnotationKind kind, PdfPoint from, PdfPoint to, PdfColor color, double width, string author);
    int AddStamp(int page, PdfRect box, byte[] imageBytes, string author);
    IReadOnlyList<AnnotationInfo> GetAnnotations(int page);
    void UpdateAnnotation(int page, int id, string? content, PdfColor? color);
    void MoveAnnotation(int page, int id, double dx, double dy);
    void DeleteAnnotation(int page, int id);

    // ---- Contenido de la página: texto ----
    IReadOnlyList<TextBlockInfo> GetTextBlocks(int page);

    /// <summary>
    /// Borra solo el texto que hay dentro de <paramref name="eraseBox"/> (los fondos, líneas e imágenes se conservan)
    /// y escribe <paramref name="text"/> dentro de <paramref name="placeBox"/> con el formato indicado.
    /// Si no cabe, reduce el tamaño de fuente hasta un 60 %. Devuelve el tamaño finalmente usado.
    /// </summary>
    double ReplaceText(int page, PdfRect eraseBox, PdfRect placeBox, string text, TextFormat format, TextAlign align, double lineHeightFactor = 0);

    /// <summary>Agrega texto nuevo a la página. Devuelve el tamaño de fuente usado.</summary>
    double AddText(int page, PdfRect box, string text, TextFormat format, TextAlign align);

    // ---- Contenido de la página: imágenes ----
    IReadOnlyList<PageImageInfo> GetImages(int page);
    void MoveImage(PageImageInfo image, PdfRect newBox);
    void DeleteImage(PageImageInfo image);

    /// <summary>Sustituye la imagen por otra, ajustada a la misma caja conservando proporciones.</summary>
    void ReplaceImage(PageImageInfo image, byte[] newImage);

    void AddImage(int page, PdfRect box, byte[] image);

    // ---- Páginas ----
    void RotatePages(IReadOnlyList<int> pages, int degrees);
    void DeletePages(IReadOnlyList<int> pages);

    /// <summary>Nuevo orden: <paramref name="newOrder"/>[i] es el índice actual de la página que quedará en la posición i.</summary>
    void ReorderPages(IReadOnlyList<int> newOrder);

    void InsertBlankPage(int at, double width, double height);

    /// <summary>Inserta todas las páginas del PDF indicado desde la posición <paramref name="at"/>. Devuelve cuántas.</summary>
    int InsertPagesFrom(string path, int at, string? password = null);

    /// <summary>Guarda las páginas indicadas como un PDF nuevo (el documento abierto no cambia).</summary>
    void ExtractPages(IReadOnlyList<int> pages, string destPath);

    // ---- Deshacer y guardar ----
    byte[] CreateSnapshot();
    void RestoreSnapshot(byte[] snapshot);

    /// <summary>Escribe el documento en <paramref name="path"/>; ese pasa a ser su nueva ruta.</summary>
    void Save(string path);
}
