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
            var pg = _doc[page];
            if (pg.GetText("dict") is not PageInfo info) return result;
            foreach (var block in info.Blocks)
            {
                if (block.Type != 0 || block.Lines is null) continue;
                var lines = new List<TextLineInfo>();
                foreach (var line in block.Lines)
                {
                    var spans = line.Spans.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
                    if (spans.Count == 0) continue;
                    // Solo se edita texto que se ve en horizontal y de izquierda a derecha (en una página girada, el texto
                    // de un documento girado a mano se ve de lado: no se puede editar sin girar la página de vuelta).
                    var dir = ToViewDirection(pg, line.Dir.X, line.Dir.Y);
                    if (dir.X < 0.98) continue;
                    var dominant = spans.OrderByDescending(s => s.Text.Length).First();
                    var text = string.Concat(spans.Select(s => s.Text)).Trim();
                    double baseline = ToViewPoint(pg, dominant.Origin).Y;
                    lines.Add(new TextLineInfo(ToView(pg, line.Bbox), text, FormatOf(dominant, page), baseline, RunsOf(spans, page)));
                }
                if (lines.Count > 0)
                {
                    var box = lines.Select(l => l.Box).Aggregate((a, b) => new PdfRect(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1)));
                    result.Add(new TextBlockInfo(page, box, lines));
                }
            }
        }
        return result;
    }

    /// <summary>Tramos con formato propio de un renglón (null si todo el renglón tiene el mismo formato).</summary>
    private IReadOnlyList<TextRun>? RunsOf(List<Span> spans, int page)
    {
        var runs = new List<TextRun>();
        foreach (var s in spans)
        {
            var f = FormatOf(s, page);
            if (runs.Count > 0 && runs[^1].Format == f) runs[^1] = runs[^1] with { Text = runs[^1].Text + s.Text };
            else runs.Add(new TextRun(s.Text, f));
        }
        if (runs.Count < 2) return null;
        runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
        runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        return runs.Where(r => r.Text.Length > 0).ToList();
    }

    // Las fuentes Type3 (PDF generados desde páginas web) no dicen su peso: se deduce midiendo el grosor de sus trazos.
    private readonly Dictionary<string, bool> _heavyType3 = [];
    private const double HeavyStemRatio = 0.125; // grosor de trazo / tamaño: normal ≈ 0,08–0,115, negrita ≈ 0,13–0,17

    private bool IsHeavyType3(Span span, int page)
    {
        var key = span.Font ?? "";
        if (_heavyType3.TryGetValue(key, out var known)) return known;
        if (span.Text.Count(char.IsLetter) < 4 || span.Size <= 0) return false; // muestra muy corta: se intentará con otro tramo
        double stem = StemRatio(page, span);
        if (double.IsNaN(stem)) return false;
        return _heavyType3[key] = stem >= HeavyStemRatio;
    }

    /// <summary>Largo típico de los tramos oscuros de una fila de píxeles (≈ grosor de los trazos) sobre el tamaño de letra.</summary>
    private double StemRatio(int page, Span span)
    {
        const int k = 4;
        try
        {
            var pg = _doc[page];
            var view = ToView(pg, span.Bbox);
            using var pix = pg.GetPixmap(matrix: new Matrix(k, k), clip: ToRect(view), colorSpace: "gray");
            var px = pix.SAMPLES;
            int n = pix.N, w = pix.Width, h = pix.Height, stride = pix.Stride;
            int bg = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) bg = Math.Max(bg, px[y * stride + x * n]);
            int rgb = span.Color;
            double lum = 0.299 * ((rgb >> 16) & 255) + 0.587 * ((rgb >> 8) & 255) + 0.114 * (rgb & 255);
            if (bg - lum < 40) return double.NaN; // texto casi del color del fondo: no se puede medir
            double threshold = (bg + lum) / 2;
            var runs = new List<int>();
            for (int y = 0; y < h; y++)
            {
                int run = 0;
                for (int x = 0; x <= w; x++)
                {
                    if (x < w && px[y * stride + x * n] < threshold) run++;
                    else if (run > 0) { runs.Add(run); run = 0; }
                }
            }
            if (runs.Count < 20) return double.NaN;
            runs.Sort();
            return runs[runs.Count / 2] / (double)k / span.Size;
        }
        catch { return double.NaN; }
    }

    /// <summary>Deduce familia, negrita, cursiva y color de un tramo de texto.</summary>
    private TextFormat FormatOf(Span span, int page)
    {
        var name = (span.Font ?? "").ToLowerInvariant();
        int flags = (int)span.Flags;
        bool bold = (flags & 16) != 0 || name.Contains("bold") || name.Contains("black") || name.Contains("heavy")
            || (name.StartsWith("type3") && IsHeavyType3(span, page));
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

    public double ReplaceText(int page, PdfRect eraseBox, PdfRect placeBox, string text, TextFormat format, TextAlign align,
        double lineHeightFactor = 0, double? baseline = null, IReadOnlyList<TextRun>? runs = null)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            double size = format.Size;
            if (runs is { Count: > 0 } && baseline is double line)
            {
                // Se comprueba que quepa ANTES de borrar: si no cabe, el renglón original queda intacto.
                var plan = PlanRuns(placeBox, runs);
                EraseText(page, eraseBox);
                size = DrawRuns(page, plan, align, line, placeBox);
            }
            else
            {
                EraseText(page, eraseBox);
                if (!string.IsNullOrWhiteSpace(text)) size = InsertFitted(page, placeBox, text, format, align, lineHeightFactor, baseline);
            }
            Invalidate();
            return size;
        }
    }

    public double AddText(int page, PdfRect box, string text, TextFormat format, TextAlign align)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            double size = InsertFitted(page, box, text, format, align, 0, null);
            Invalidate();
            return size;
        }
    }

    /// <summary>Elimina solo el texto de la zona: sin relleno (conserva el fondo) y sin tocar imágenes ni líneas.</summary>
    private void EraseText(int page, PdfRect box)
    {
        var p = _doc[page];
        p.AddRedactAnnot(ToUnrotated(p, box), null!, null!, 11f, 0, null!, null!, false);
        p.ApplyRedactions(images: RedactImageNone, graphics: RedactLineArtNone, text: RedactTextRemove);
    }

    private double InsertFitted(int page, PdfRect box, string text, TextFormat format, TextAlign align, double lineHeightFactor, double? baseline)
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
            if (baseline is double target)
            {
                // Cada fuente pone su primera línea base a una distancia distinta del borde superior de la caja.
                // Se mide y se desplaza la caja para que caiga justo donde estaba la del texto original.
                double dy = target - (rect.Y0 + FirstBaselineOffset(fontFile, fontName, size, lineHeightFactor));
                rect = rect with { Y0 = rect.Y0 + dy, Y1 = rect.Y1 + dy };
            }
            var result = p.InsertTextbox(ToUnrotated(p, rect), text, (int)align, 0f,
                [format.Color.R / 255f, format.Color.G / 255f, format.Color.B / 255f], 0, 0f, 1f, null!,
                fontFile!, fontName, (float)size, lineHeight: lineHeightFactor > 0 ? (float)lineHeightFactor : null, rotate: p.Rotation);
            // Si el texto no cabe, MuPDF no escribe nada y devuelve un valor negativo.
            if (result.Rc >= 0) return size;
            size = Math.Round(size * FontShrinkStep, 2);
            if (size < min)
                throw new InvalidOperationException("El texto nuevo no cabe en el espacio disponible. Acórtalo o agrándalo dejando más espacio.");
        }
    }

    private sealed record RunPlan(IReadOnlyList<TextRun> Runs, List<(string? File, string Name)> Fonts, double Scale, double Total);

    /// <summary>Elige las fuentes y la escala con que cabe un renglón de varios tramos en la caja; lanza si no cabe.</summary>
    private RunPlan PlanRuns(PdfRect box, IReadOnlyList<TextRun> runs)
    {
        var fonts = runs.Select(r => FontResolver.Resolve(r.Format, r.Text)).ToList();
        double scale = 1;
        while (true)
        {
            double total = 0;
            for (int i = 0; i < runs.Count; i++) total += TextWidth(fonts[i], runs[i].Text, runs[i].Format.Size * scale);
            if (total <= box.Width + 0.5) return new RunPlan(runs, fonts, scale, total);
            scale *= FontShrinkStep;
            if (scale < MinFontScale)
                throw new InvalidOperationException("El texto nuevo no cabe en el espacio disponible. Acórtalo o agrándalo dejando más espacio.");
        }
    }

    /// <summary>Escribe un renglón formado por varios tramos, cada uno con su formato, sobre la línea base indicada.</summary>
    private double DrawRuns(int page, RunPlan plan, TextAlign align, double baseline, PdfRect box)
    {
        double x = align switch
        {
            TextAlign.Right => box.X1 - plan.Total,
            TextAlign.Center => box.X0 + (box.Width - plan.Total) / 2,
            _ => box.X0,
        };
        for (int i = 0; i < plan.Runs.Count; i++)
        {
            var (file, name) = plan.Fonts[i];
            var f = plan.Runs[i].Format;
            float size = (float)(f.Size * plan.Scale);
            var pg = _doc[page];
            pg.InsertText(ToUnrotated(pg, x, baseline), plan.Runs[i].Text, size, name,
                [f.Color.R / 255f, f.Color.G / 255f, f.Color.B / 255f], pg.Rotation, 0, 1f, null!, file!);
            x += TextWidth(plan.Fonts[i], plan.Runs[i].Text, size);
        }
        return Math.Round(plan.Runs.Max(r => r.Format.Size) * plan.Scale, 2);
    }

    private static double TextWidth((string? File, string Name) font, string text, double size)
    {
        if (font.File is null) return Utils.GetTextLength(text, font.Name, (float)size);
        return new MuPDF.NET.Font(fontFile: font.File).TextLength(text, (float)size);
    }

    private static readonly Dictionary<(string?, string, double, double), double> BaselineOffsets = [];

    /// <summary>
    /// Distancia entre el borde superior de una caja y la línea base del primer renglón que MuPDF escribe con esa
    /// fuente y tamaño. Se mide escribiendo una "H" en un documento de prueba que se descarta.
    /// </summary>
    private static double FirstBaselineOffset(string? fontFile, string fontName, double size, double lineHeightFactor)
    {
        var key = (fontFile, fontName, size, lineHeightFactor);
        if (BaselineOffsets.TryGetValue(key, out var cached)) return cached;

        double offset = size * 0.9; // valor razonable si la medición fallara
        var probe = new Document();
        try
        {
            probe.NewPage(width: 400, height: 400);
            probe[0].InsertTextbox(new MuPDF.NET.Rect(10, 100, 390, 300), "H", 0, 0f, [0f, 0f, 0f], 0, 0f, 1f, null!,
                fontFile!, fontName, (float)size, lineHeight: lineHeightFactor > 0 ? (float)lineHeightFactor : null);
            var span = (probe[0].GetText("dict") as PageInfo)?.Blocks
                .Where(b => b.Lines is not null).SelectMany(b => b.Lines).SelectMany(l => l.Spans).FirstOrDefault();
            if (span is not null) offset = span.Origin.Y - 100;
        }
        finally { probe.Close(); }

        BaselineOffsets[key] = offset;
        return offset;
    }

    // ---------------- Imágenes ----------------

    public IReadOnlyList<PageImageInfo> GetImages(int page)
    {
        var result = new List<PageImageInfo>();
        lock (NativeLock)
        {
            using var _ = Invariant();
            foreach (var b in _doc[page].GetImageInfo(true))
                if (b.Xref > 0) result.Add(new PageImageInfo(page, b.Xref, ToView(_doc[page], b.Bbox)));
        }
        return result;
    }

    public void MoveImage(PageImageInfo image, PdfRect newBox)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
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
            RemoveImageAt(image);
            Invalidate();
        }
    }

    public void ReplaceImage(PageImageInfo image, byte[] newImage)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
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

    /// <summary>
    /// Quita SOLO la imagen indicada. MuPDF borra cualquier imagen que toque el rectángulo, así que antes se guardan las
    /// otras imágenes que lo tocan y después se vuelven a colocar en su sitio (quedan por encima del resto de la página).
    /// </summary>
    private void RemoveImageAt(PageImageInfo image)
    {
        var b = image.Box;
        var inner = new PdfRect(b.X0 + 1, b.Y0 + 1, Math.Max(b.X0 + 2, b.X1 - 1), Math.Max(b.Y0 + 2, b.Y1 - 1));
        var p = _doc[image.PageIndex];

        var others = new List<(PdfRect Box, byte[] Bytes, byte[]? Mask)>();
        foreach (var info in p.GetImageInfo(true))
        {
            if (info.Xref <= 0) continue;
            var box = ToView(p, info.Bbox);
            bool isTarget = info.Xref == image.Xref
                && Math.Abs(box.X0 - b.X0) < 1 && Math.Abs(box.Y0 - b.Y0) < 1 && Math.Abs(box.X1 - b.X1) < 1 && Math.Abs(box.Y1 - b.Y1) < 1;
            if (isTarget || !box.Intersects(inner)) continue;
            var (bytes, mask) = ExtractImageData(info.Xref);
            others.Add((box, bytes, mask));
        }

        p.AddRedactAnnot(ToUnrotated(p, inner), null!, null!, 11f, 0, null!, null!, false);
        p.ApplyRedactions(images: RedactImageRemove, graphics: RedactLineArtNone, text: RedactTextNone);

        foreach (var o in others) Place(image.PageIndex, o.Box, o.Bytes, o.Mask, keepProportion: false);
    }

    private void Place(int page, PdfRect box, byte[] image, byte[]? mask, bool keepProportion)
    {
        var p = _doc[page];
        p.InsertImage(ToUnrotated(p, box), stream: image, mask: mask, rotate: p.Rotation, keepProportion: keepProportion);
    }
}
