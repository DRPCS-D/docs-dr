using DocsDR.Core;
using MuPDF.NET;

namespace DocsDR.Pdf;

/// <summary>Modificación del documento: anotaciones, operaciones de página, instantáneas y guardado.</summary>
public sealed partial class MuPdfDocument : IPdfEditor
{
    // ---------------- Anotaciones ----------------

    private static float[] Rgb(PdfColor c) => [c.R / 255f, c.G / 255f, c.B / 255f];

    private static PdfColor? ToColor(float[]? rgb) =>
        rgb is { Length: 3 } ? new PdfColor((byte)Math.Round(rgb[0] * 255), (byte)Math.Round(rgb[1] * 255), (byte)Math.Round(rgb[2] * 255)) : null;

    private static void Stamp(Annot a, PdfColor color, string author, string? content = null)
    {
        a.SetColors(stroke: Rgb(color));
        a.SetInfo(content ?? "", author, null!, null!, null!);
        a.Update();
    }

    public int AddTextMarkup(int page, AnnotationKind kind, IReadOnlyList<PdfRect> lines, PdfColor color, string author)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            // Las esquinas se convierten una a una y en el orden de la vista (sup. izq., sup. der., inf. izq., inf. der.).
            var quads = lines.Select(r => new Quad(
                ToUnrotated(p, r.X0, r.Y0), ToUnrotated(p, r.X1, r.Y0), ToUnrotated(p, r.X0, r.Y1), ToUnrotated(p, r.X1, r.Y1))).ToArray();
            var annot = kind switch
            {
                AnnotationKind.Highlight => p.AddHighlightAnnot(quads),
                AnnotationKind.Underline => p.AddUnderlineAnnot(quads),
                AnnotationKind.StrikeOut => p.AddStrikeoutAnnot(quads),
                _ => throw new ArgumentException("Tipo de marcado no válido", nameof(kind)),
            };
            Stamp(annot, color, author);
            Invalidate();
            return annot.Xref;
        }
    }

    public int AddNote(int page, PdfPoint at, string text, PdfColor color, string author)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            // MuPDF recibe el punto en la vista, pero ancla el icono (16×16) en el espacio sin girar: en una página girada
            // queda corrido 16 pt en uno o en los dos ejes (y SetRect no lo mueve). Se compensa desplazando el punto.
            int rot = p.Rotation;
            double dx = rot is 90 or 180 ? -16 : 0, dy = rot is 180 or 270 ? -16 : 0;
            var annot = p.AddTextAnnot(new MuPDF.NET.Point((float)(at.X + dx), (float)(at.Y + dy)), text);
            Stamp(annot, color, author, text);
            Invalidate();
            return annot.Xref;
        }
    }

    public int AddFreeText(int page, PdfRect box, string text, double fontSize, PdfColor color, string author)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            var p = _doc[page];
            var annot = p.AddFreeTextAnnot(ToUnrotated(p, box), text, fontSize: (float)fontSize, textColor: Rgb(color), rotate: p.Rotation);
            annot.SetInfo(text, author, null!, null!, null!);
            Invalidate();
            return annot.Xref;
        }
    }

    public int AddInk(int page, IReadOnlyList<PdfPoint> stroke, PdfColor color, double width, string author)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            var pts = stroke.Select(s => ToUnrotated(p, s.X, s.Y)).ToArray();
            var annot = p.AddInkAnnot([pts]);
            annot.SetBorder(width: (float)width);
            Stamp(annot, color, author);
            Invalidate();
            return annot.Xref;
        }
    }

    public int AddShape(int page, AnnotationKind kind, PdfPoint from, PdfPoint to, PdfColor color, double width, string author)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            Annot annot;
            if (kind is AnnotationKind.Line or AnnotationKind.Arrow)
            {
                annot = p.AddLineAnnot(new MuPDF.NET.Point((float)from.X, (float)from.Y), new MuPDF.NET.Point((float)to.X, (float)to.Y));
                annot.SetLineEnds(PdfLineEnding.PDF_ANNOT_LE_NONE,
                    kind == AnnotationKind.Arrow ? PdfLineEnding.PDF_ANNOT_LE_CLOSED_ARROW : PdfLineEnding.PDF_ANNOT_LE_NONE);
            }
            else
            {
                var r = PdfRect.FromPoints(from.X, from.Y, to.X, to.Y);
                var rect = new MuPDF.NET.Rect((float)r.X0, (float)r.Y0, (float)r.X1, (float)r.Y1);
                annot = kind == AnnotationKind.Ellipse ? p.AddCircleAnnot(rect) : p.AddRectAnnot(rect);
            }
            annot.SetBorder(width: (float)width);
            Stamp(annot, color, author);
            Invalidate();
            return annot.Xref;
        }
    }

    public int AddStamp(int page, PdfRect box, byte[] imageBytes, string author)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            // La imagen de un sello se dibuja en el espacio sin girar: se gira al revés que la página para que se vea derecha.
            var annot = p.AddStampAnnot(ToUnrotated(p, box), p.Rotation == 0 ? imageBytes : RotateImage(imageBytes, (360 - p.Rotation) % 360));
            annot.SetInfo("", author, null!, null!, null!);
            annot.Update();
            Invalidate();
            return annot.Xref;
        }
    }

    /// <summary>Gira una imagen (PNG/JPEG…) un múltiplo de 90° en sentido horario y la devuelve como PNG.</summary>
    private static byte[] RotateImage(byte[] bytes, int degreesClockwise)
    {
        var src = new Pixmap(bytes);
        int w = src.Width, h = src.Height, n = src.N, stride = src.Stride;
        var s = src.SAMPLES;
        bool swap = degreesClockwise is 90 or 270;
        int nw = swap ? h : w, nh = swap ? w : h;
        var dst = new byte[nw * nh * n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                switch (degreesClockwise)
                {
                    case 90: nx = h - 1 - y; ny = x; break;
                    case 180: nx = w - 1 - x; ny = h - 1 - y; break;
                    default: nx = y; ny = w - 1 - x; break; // 270
                }
                Buffer.BlockCopy(s, y * stride + x * n, dst, (ny * nw + nx) * n, n);
            }
        var rotated = new Pixmap(src.Colorspace, nw, nh, dst, src.Alpha != 0);
        return rotated.ToBytes("png");
    }

    public IReadOnlyList<AnnotationInfo> GetAnnotations(int page)
    {
        var result = new List<AnnotationInfo>();
        lock (NativeLock)
        {
            var annots = _doc[page].GetAnnots();
            if (annots is null) return result;
            foreach (var a in annots)
            {
                var kind = KindOf(a);
                if (kind is null) continue;
                var r = ToView(_doc[page], a.Rect);
                var color = ToColor(a.StrokeColor);
                // El texto libre no usa trazo: su color es el del texto.
                result.Add(new AnnotationInfo(page, a.Xref, kind.Value, a.Info?.Content ?? "", a.Info?.Title ?? "",
                    r, color));
            }
        }
        return result;
    }

    /// <summary>Tipos que mostramos; enlaces, ventanas emergentes y campos de formulario quedan fuera.</summary>
    private static AnnotationKind? KindOf(Annot a) => a.AnnotationType switch
    {
        AnnotationType.Highlight => AnnotationKind.Highlight,
        AnnotationType.Underline => AnnotationKind.Underline,
        AnnotationType.StrikeOut => AnnotationKind.StrikeOut,
        AnnotationType.Text => AnnotationKind.Note,
        AnnotationType.FreeText => AnnotationKind.FreeText,
        AnnotationType.Ink => AnnotationKind.Ink,
        AnnotationType.Square => AnnotationKind.Rectangle,
        AnnotationType.Circle => AnnotationKind.Ellipse,
        AnnotationType.Line => a.LineEnds is { } le && (le.Item2 != 0 || le.Item1 != 0) ? AnnotationKind.Arrow : AnnotationKind.Line,
        AnnotationType.Stamp => AnnotationKind.Stamp,
        AnnotationType.Link or AnnotationType.Popup or AnnotationType.Widget => null,
        _ => AnnotationKind.Other,
    };

    private Annot Load(int page, int id) =>
        _doc[page].LoadAnnot(id) ?? throw new InvalidOperationException("La anotación ya no existe.");

    public void UpdateAnnotation(int page, int id, string? content, PdfColor? color)
    {
        lock (NativeLock)
        {
            var a = Load(page, id);
            if (content is not null)
            {
                var info = a.Info;
                info.Content = content;
                a.SetInfo(info: info);
            }
            if (color is { } c)
            {
                if (a.AnnotationType == AnnotationType.FreeText) a.Update(textColor: Rgb(c));
                else { a.SetColors(stroke: Rgb(c)); }
            }
            a.Update();
            Invalidate();
        }
    }

    public void MoveAnnotation(int page, int id, double dx, double dy)
    {
        lock (NativeLock)
        {
            var a = Load(page, id);
            var r = a.Rect;
            // El desplazamiento viene de la vista: se gira al espacio sin girar en el que está la anotación.
            var (ux, uy) = ToUnrotatedVector(_doc[page], dx, dy);
            a.SetRect(new MuPDF.NET.Rect(r.X0 + (float)ux, r.Y0 + (float)uy, r.X1 + (float)ux, r.Y1 + (float)uy));
            a.Update();
            Invalidate();
        }
    }

    public void DeleteAnnotation(int page, int id)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            p.DeleteAnnot(Load(page, id));
            Invalidate();
        }
    }

    // ---------------- Páginas ----------------

    public void RotatePages(IReadOnlyList<int> pages, int degrees)
    {
        lock (NativeLock)
        {
            foreach (var i in pages)
            {
                var p = _doc[i];
                p.SetRotation(((p.Rotation + degrees) % 360 + 360) % 360);
            }
            Invalidate();
        }
    }

    public void DeletePages(IReadOnlyList<int> pages)
    {
        lock (NativeLock)
        {
            if (pages.Distinct().Count() >= _doc.PageCount)
                throw new InvalidOperationException("No se pueden eliminar todas las páginas del documento.");
            _doc.DeletePages(pages.Distinct().OrderDescending().ToArray());
            Invalidate();
        }
    }

    public void ReorderPages(IReadOnlyList<int> newOrder)
    {
        lock (NativeLock)
        {
            _doc.Select(newOrder.ToArray());
            Invalidate();
        }
    }

    public void InsertBlankPage(int at, double width, double height)
    {
        lock (NativeLock)
        {
            _doc.NewPage(pno: at, width: (float)width, height: (float)height);
            Invalidate();
        }
    }

    public int InsertPagesFrom(string path, int at, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        lock (NativeLock)
        {
            Document src;
            try { src = OpenBytes(bytes, password); }
            catch (PdfPasswordRequiredException) { throw new PdfPasswordRequiredException(path); }
            try
            {
                int count = src.PageCount;
                _doc.InsertPdf(src, fromPage: 0, toPage: count - 1, startAt: at, links: true, annots: true);
                Invalidate();
                return count;
            }
            finally { src.Close(); }
        }
    }

    public void ExtractPages(IReadOnlyList<int> pages, string destPath)
    {
        byte[] bytes;
        lock (NativeLock)
        {
            var dest = new Document();
            try
            {
                // Cada tramo consecutivo se copia de una vez, respetando el orden pedido.
                foreach (var run in Runs(pages))
                    dest.InsertPdf(_doc, fromPage: run.From, toPage: run.To, startAt: dest.PageCount, links: true, annots: true);
                bytes = dest.Write(garbage: true, deflate: true);
            }
            finally { dest.Close(); }
        }
        WriteAtomic(destPath, bytes);
    }

    /// <summary>Une varios PDF en uno nuevo. Los protegidos con contraseña provocan <see cref="PdfPasswordRequiredException"/>.</summary>
    internal static void MergeFiles(IReadOnlyList<string> files, string destPath)
    {
        // Se lee todo antes de tomar el candado nativo, para no bloquear el resto de la app en disco.
        var sources = files.Select(f => (Path: f, Bytes: File.ReadAllBytes(f))).ToList();
        byte[] bytes;
        lock (NativeLock)
        {
            var dest = new Document();
            try
            {
                foreach (var (path, data) in sources)
                {
                    var src = new Document(stream: data, fileType: "pdf");
                    try
                    {
                        if (src.NeedsPass) throw new PdfPasswordRequiredException(path);
                        dest.InsertPdf(src, fromPage: 0, toPage: src.PageCount - 1, startAt: dest.PageCount, links: true, annots: true);
                    }
                    finally { src.Close(); }
                }
                bytes = dest.Write(garbage: true, deflate: true);
            }
            finally { dest.Close(); }
        }
        WriteAtomic(destPath, bytes);
    }

    private static IEnumerable<(int From, int To)> Runs(IReadOnlyList<int> pages)
    {
        int i = 0;
        while (i < pages.Count)
        {
            int start = pages[i];
            int end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1) { end++; i++; }
            yield return (start, end);
            i++;
        }
    }

    // ---------------- Instantáneas y guardado ----------------

    public byte[] CreateSnapshot()
    {
        // Con KEEP el documento restaurado sigue protegido (se reabre con la contraseña guardada).
        lock (NativeLock) { return _doc.Write(encryption: MuPDF.NET.Constants.PDF_ENCRYPT_KEEP); }
    }

    public void RestoreSnapshot(byte[] snapshot)
    {
        lock (NativeLock)
        {
            var restored = OpenBytes(snapshot, _password);
            _doc.Close();
            _doc = restored;
            Invalidate();
        }
    }

    public void Save(string path)
    {
        byte[] bytes;
        // Sin indicarlo, MuPDF guarda sin cifrado: un PDF protegido perdería su contraseña en silencio.
        lock (NativeLock) { bytes = _doc.Write(garbage: true, deflate: true, encryption: MuPDF.NET.Constants.PDF_ENCRYPT_KEEP); }
        WriteAtomic(path, bytes);
        FilePath = path;
    }

    /// <summary>Escribe en un temporal y lo mueve encima: un fallo a medias no destruye el archivo original.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".docsdr.tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Descarta lo calculado sobre el contenido anterior (palabras, líneas, OCR).</summary>
    private void Invalidate() => _contentCache.Clear();
}
