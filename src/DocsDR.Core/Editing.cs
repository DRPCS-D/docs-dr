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

/// <summary>
/// Fuente original de un texto del PDF (nombre sin el prefijo de subconjunto, p. ej. «Calibri-Bold») y su estilo. Sirve para
/// escribir el texto nuevo con la misma fuente (o una equivalente) en lugar de la familia genérica.
/// </summary>
public sealed record FontFace(string Name, bool Bold, bool Italic);

/// <summary>Formato de un texto: familia (Arial, Times New Roman, Courier New), tamaño en puntos, color y estilo.</summary>
public sealed record TextFormat(string Family, double Size, PdfColor Color, bool Bold, bool Italic, FontFace? Face = null)
{
    public const string Sans = "Arial", Serif = "Times New Roman", Mono = "Courier New";
    public static IReadOnlyList<string> Families { get; } = [Sans, Serif, Mono];
}

/// <summary>Un renglón de texto del documento con su formato dominante y la Y de su línea base.</summary>
public sealed record TextLineInfo(PdfRect Box, string Text, TextFormat Format, double Baseline, IReadOnlyList<TextRun>? Runs = null);

/// <summary>Un tramo de un renglón con su propio formato (un renglón puede mezclar colores, negritas o tamaños).</summary>
public sealed record TextRun(string Text, TextFormat Format);

/// <summary>Reparte un texto editado entre los tramos originales para que lo que no se tocó conserve su formato.</summary>
public static class TextRunMapper
{
    public static IReadOnlyList<TextRun> Remap(IReadOnlyList<TextRun> old, string newText)
    {
        string oldText = string.Concat(old.Select(r => r.Text));
        int max = Math.Min(oldText.Length, newText.Length);
        int prefix = 0;
        while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        // Lo que reemplaza texto toma el formato del primer carácter reemplazado; lo que se inserta sin reemplazar
        // nada, el del carácter anterior (como al teclear en un editor de texto).
        bool replaces = oldText.Length - suffix > prefix;
        var midFormat = FormatAt(old, replaces ? prefix : Math.Max(0, prefix - 1));
        var result = new List<TextRun>();
        void Add(string text, TextFormat f)
        {
            if (text.Length == 0) return;
            if (result.Count > 0 && result[^1].Format == f) result[^1] = result[^1] with { Text = result[^1].Text + text };
            else result.Add(new TextRun(text, f));
        }

        int pos = 0;
        foreach (var r in old)
        {
            int end = pos + r.Text.Length;
            if (pos < prefix) Add(r.Text[..(Math.Min(end, prefix) - pos)], r.Format);
            pos = end;
        }
        Add(newText.Substring(prefix, newText.Length - prefix - suffix), midFormat);
        int suffixStart = oldText.Length - suffix;
        pos = 0;
        foreach (var r in old)
        {
            int end = pos + r.Text.Length;
            if (end > suffixStart) Add(r.Text[(Math.Max(pos, suffixStart) - pos)..], r.Format);
            pos = end;
        }
        return result;
    }

    private static TextFormat FormatAt(IReadOnlyList<TextRun> runs, int index)
    {
        int pos = 0;
        foreach (var r in runs)
        {
            pos += r.Text.Length;
            if (index < pos) return r.Format;
        }
        return runs[^1].Format;
    }
}

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
    /// <param name="baseline">
    /// Y de la línea base que debe tener el primer renglón (normalmente la del texto original), para que el texto
    /// nuevo quede exactamente a la misma altura aunque cambie la fuente. Null = alinear con el borde de la caja.
    /// </param>
    /// <param name="runs">
    /// Renglón con formatos mezclados: cada tramo se escribe con su formato, uno tras otro, en una sola línea
    /// (requiere <paramref name="baseline"/>). Si es null se escribe todo con <paramref name="format"/>.
    /// </param>
    double ReplaceText(int page, PdfRect eraseBox, PdfRect placeBox, string text, TextFormat format, TextAlign align,
        double lineHeightFactor = 0, double? baseline = null,
        IReadOnlyList<TextRun>? runs = null);

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

    // ---- OCR: PDF escaneado → PDF con texto buscable ----

    /// <summary>true si están instalados los datos de idioma del OCR (tessdata).</summary>
    bool OcrAvailable { get; }

    /// <summary>¿La página es una imagen escaneada sin texto (y no está girada)? Solo esas páginas se reconocen.</summary>
    bool NeedsOcr(int page);

    /// <summary>Reconoce el texto de la página (lento, se puede llamar desde otro hilo). Null si no hay datos de OCR.</summary>
    IReadOnlyList<TextWord>? RecognizePage(int page);

    /// <summary>Escribe las palabras reconocidas como texto invisible sobre la imagen: la página se ve igual pero se puede buscar y copiar.</summary>
    void AddInvisibleText(int page, IReadOnlyList<TextWord> words);

    // ---- Deshacer y guardar ----
    byte[] CreateSnapshot();
    void RestoreSnapshot(byte[] snapshot);

    /// <summary>Escribe el documento en <paramref name="path"/>; ese pasa a ser su nueva ruta.</summary>
    void Save(string path);
}
