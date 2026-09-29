using System.Globalization;
using DocsDR.Core;
using MuPDF.NET;
using TextAlign = DocsDR.Core.TextAlign;

namespace DocsDR.Pdf;

/// <summary>Edición del contenido existente de la página: texto e imágenes.</summary>
public sealed partial class MuPdfDocument
{
    // Opciones de ApplyRedactions (PDF_REDACT_* de MuPDF).
    private const int RedactImageNone = 0, RedactImageRemove = 1;
    private const int RedactLineArtNone = 0;
    private const int RedactTextRemove = 0, RedactTextNone = 1;

    private const double MinFontScale = 0.6;   // hasta dónde se reduce la fuente para que un texto quepa
    private const double FontShrinkStep = 0.94;
    private const double LineBoxFactor = 1.75;   // altura mínima de la caja de un renglón, en múltiplos del tamaño
    private const double SingleLineBoxFactor = 3.2; // cajas más bajas que esto se consideran "de un solo renglón"

    /// <summary>
    /// MuPDF.NET escribe los números del contenido de la página con la cultura del sistema: en configuraciones
    /// con coma decimal (es-PY, es-ES…) "0,8" no es un número válido en un PDF y se corrompen colores, tamaños y
    /// posiciones. Toda operación que genera contenido se ejecuta con la cultura invariante.
    /// </summary>
    private sealed class InvariantCultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public InvariantCultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }

    private static InvariantCultureScope Invariant() => new();

    private static MuPDF.NET.Rect ToRect(PdfRect r) => new((float)r.X0, (float)r.Y0, (float)r.X1, (float)r.Y1);

    private static PdfRect FromRect(MuPDF.NET.Rect r) => new(r.X0, r.Y0, r.X1, r.Y1);

    /// <summary>Las coordenadas de los métodos de inserción de MuPDF son las de la página sin girar.</summary>
    private void RequireUnrotated(int page)
    {
        if (_doc[page].Rotation != 0)
            throw new NotSupportedException("La edición de texto e imágenes no está disponible en páginas giradas. Devuelve la página a su orientación original (rotar en sentido contrario) y vuelve a intentarlo.");
    }

    // ---------------- Texto ----------------

    public IReadOnlyList<TextBlockInfo> GetTextBlocks(int page)
    {
        var result = new List<TextBlockInfo>();
        lock (NativeLock)
        {
            using var _ = Invariant();
            if (_doc[page].GetText("dict") is not PageInfo info) return result;
            foreach (var block in info.Blocks)
            {
                if (block.Type != 0 || block.Lines is null) continue;
                var lines = new List<TextLineInfo>();
                foreach (var line in block.Lines)
                {
                    var spans = line.Spans.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
                    if (spans.Count == 0) continue;
                    var dominant = spans.OrderByDescending(s => s.Text.Length).First();
                    var text = string.Concat(spans.Select(s => s.Text)).Trim();
                    lines.Add(new TextLineInfo(FromRect(line.Bbox), text, FormatOf(dominant)));
                }
                if (lines.Count > 0) result.Add(new TextBlockInfo(page, FromRect(block.Bbox), lines));
            }
        }
        return result;
    }

    /// <summary>Deduce familia, negrita, cursiva y color de un tramo de texto.</summary>
    private static TextFormat FormatOf(Span span)
    {
        var name = (span.Font ?? "").ToLowerInvariant();
        int flags = (int)span.Flags;
        bool bold = (flags & 16) != 0 || name.Contains("bold") || name.Contains("black") || name.Contains("heavy");
        bool italic = (flags & 2) != 0 || name.Contains("italic") || name.Contains("oblique");
        string family =
            (flags & 8) != 0 || name.Contains("cour") || name.Contains("mono") || name.Contains("consol") ? TextFormat.Mono
            : !name.Contains("sans") && ((flags & 4) != 0 || name.Contains("times") || name.Contains("serif") || name.Contains("georgia")
                || name.Contains("garamond") || name.Contains("palatino") || name.Contains("cambria") || name.Contains("roman")) ? TextFormat.Serif
            : TextFormat.Sans;
        int rgb = span.Color;
        var color = new PdfColor((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return new TextFormat(family, Math.Round(span.Size, 1), color, bold, italic);
    }

    public double ReplaceText(int page, PdfRect eraseBox, PdfRect placeBox, string text, TextFormat format, TextAlign align, double lineHeightFactor = 0)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(page);
            EraseText(page, eraseBox);
            double size = format.Size;
            if (!string.IsNullOrWhiteSpace(text)) size = InsertFitted(page, placeBox, text, format, align, lineHeightFactor);
            Invalidate();
            return size;
        }
    }

    public double AddText(int page, PdfRect box, string text, TextFormat format, TextAlign align)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(page);
            double size = InsertFitted(page, box, text, format, align, 0);
            Invalidate();
            return size;
        }
    }

    /// <summary>Elimina solo el texto de la zona: sin relleno (conserva el fondo) y sin tocar imágenes ni líneas.</summary>
    private void EraseText(int page, PdfRect box)
    {
        var p = _doc[page];
        p.AddRedactAnnot(ToRect(box), null!, null!, 11f, 0, null!, null!, false);
        p.ApplyRedactions(images: RedactImageNone, graphics: RedactLineArtNone, text: RedactTextRemove);
    }

    private double InsertFitted(int page, PdfRect box, string text, TextFormat format, TextAlign align, double lineHeightFactor)
    {
        var (fontFile, fontName) = FontResolver.Resolve(format, text);
        double size = format.Size;
        double min = format.Size * MinFontScale;
        while (true)
        {
            // Se pide la página de nuevo cada vez: un objeto de página reutilizado puede quedar desactualizado.
            var p = _doc[page];
            // MuPDF exige ~1,6 veces el tamaño de la letra para escribir un renglón: en una caja de una sola línea
            // se garantiza esa altura antes de pensar en reducir la fuente.
            var rect = box.Height < size * SingleLineBoxFactor
                ? box with { Y1 = Math.Max(box.Y1, box.Y0 + size * LineBoxFactor) }
                : box;
            var result = p.InsertTextbox(ToRect(rect), text, (int)align, 0f,
                [format.Color.R / 255f, format.Color.G / 255f, format.Color.B / 255f], 0, 0f, 1f, null!,
                fontFile!, fontName, (float)size, lineHeight: lineHeightFactor > 0 ? (float)lineHeightFactor : null);
            // Si el texto no cabe, MuPDF no escribe nada y devuelve un valor negativo.
            if (result.Rc >= 0) return size;
            size = Math.Round(size * FontShrinkStep, 2);
            if (size < min)
                throw new InvalidOperationException("El texto nuevo no cabe en el espacio disponible. Acórtalo o agrándalo dejando más espacio.");
        }
    }

    // ---------------- Imágenes ----------------

    public IReadOnlyList<PageImageInfo> GetImages(int page)
    {
        var result = new List<PageImageInfo>();
        lock (NativeLock)
        {
            using var _ = Invariant();
            foreach (var b in _doc[page].GetImageInfo(true))
                if (b.Xref > 0) result.Add(new PageImageInfo(page, b.Xref, FromRect(b.Bbox)));
        }
        return result;
    }

    public void MoveImage(PageImageInfo image, PdfRect newBox)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(image.PageIndex);
            var (bytes, mask) = ExtractImageData(image.Xref);
            RemoveImageAt(image);
            Place(image.PageIndex, newBox, bytes, mask, keepProportion: false);
            Invalidate();
        }
    }

    public void DeleteImage(PageImageInfo image)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(image.PageIndex);
            RemoveImageAt(image);
            Invalidate();
        }
    }

    public void ReplaceImage(PageImageInfo image, byte[] newImage)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(image.PageIndex);
            RemoveImageAt(image);
            Place(image.PageIndex, image.Box, newImage, null, keepProportion: true);
            Invalidate();
        }
    }

    public void AddImage(int page, PdfRect box, byte[] image)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(page);
            Place(page, box, image, null, keepProportion: true);
            Invalidate();
        }
    }

    private (byte[] Image, byte[]? Mask) ExtractImageData(int xref)
    {
        var img = _doc.ExtractImage(xref) ?? throw new InvalidOperationException("No se pudo leer la imagen del documento.");
        byte[]? mask = img.Smask > 0 ? _doc.ExtractImage(img.Smask)?.Image : null; // máscara de transparencia
        return (img.Image, mask);
    }

    /// <summary>Quita las imágenes que tocan la caja (sin tocar el texto ni los gráficos vectoriales).</summary>
    private void RemoveImageAt(PageImageInfo image)
    {
        var b = image.Box;
        var inner = new PdfRect(b.X0 + 1, b.Y0 + 1, Math.Max(b.X0 + 2, b.X1 - 1), Math.Max(b.Y0 + 2, b.Y1 - 1));
        var p = _doc[image.PageIndex];
        p.AddRedactAnnot(ToRect(inner), null!, null!, 11f, 0, null!, null!, false);
        p.ApplyRedactions(images: RedactImageRemove, graphics: RedactLineArtNone, text: RedactTextNone);
    }

    private void Place(int page, PdfRect box, byte[] image, byte[]? mask, bool keepProportion)
    {
        _doc[page].InsertImage(ToRect(box), stream: image, mask: mask, keepProportion: keepProportion);
    }
}
