using DocsDR.Core;
using MuPDF.NET;

namespace DocsDR.Pdf;

/// <summary>OCR a PDF con texto buscable, y creación de PDF a partir de imágenes.</summary>
public sealed partial class MuPdfDocument
{
    private const int OcrDpiForSearchable = 300;
    private const float A4Short = 595f, A4Long = 842f;

    public bool OcrAvailable => TessdataLocator.Find() is not null;

    public bool NeedsOcr(int page)
    {
        lock (NativeLock)
        {
            var p = _doc[page];
            if (p.Rotation != 0) return false;
            return IsScanned(p, ToWords(p.GetTextWords(sort: true), p));
        }
    }

    public IReadOnlyList<TextWord>? RecognizePage(int page)
    {
        var tess = TessdataLocator.Find();
        if (tess is null) return null;
        lock (NativeLock)
        {
            using var _ = Invariant();
            var p = _doc[page];
            var tp = p.GetTextPageOcr(flags: 0, language: tess.Value.Languages, dpi: OcrDpiForSearchable, full: true, tessdata: tess.Value.Path);
            return ToWords(p.GetTextWords(textpage: tp, sort: true), p);
        }
    }

    public void AddInvisibleText(int page, IReadOnlyList<TextWord> words)
    {
        lock (NativeLock)
        {
            using var _ = Invariant();
            RequireUnrotated(page);
            foreach (var w in words)
            {
                // La fuente estándar solo tiene Latin-1: lo demás se cambia por «?» (el texto es invisible y solo sirve para buscar).
                var text = new string(w.Text.Select(c => c >= 0x20 && c <= 0xFF && !(c >= 0x7F && c < 0xA0) ? c : '?').ToArray());
                double h = w.Box.Height, width = w.Box.Width;
                if (text.Length == 0 || h <= 0 || width <= 0) continue;

                float size = (float)Math.Clamp(h * 0.85, 2, 200);
                float natural = Utils.GetTextLength(text, "helv", size);
                if (natural <= 0) continue;
                var origin = new MuPDF.NET.Point((float)w.Box.X0, (float)(w.Box.Y1 - h * 0.22));
                // Se estira/encoge el texto a lo ancho de la palabra para que la selección coincida con la imagen.
                var morph = new Morph(origin, new Matrix((float)(width / natural), 0, 0, 1, 0, 0));
                _doc[page].InsertText(origin, text, size, "helv", [0f, 0f, 0f], 0, 3 /* invisible */, 1f, null!, null!, morph: morph);
            }
            Invalidate();
        }
    }

    internal static void ImagesToPdfFile(IReadOnlyList<string> images, string destPath)
    {
        if (images.Count == 0) throw new ArgumentException("No hay imágenes.", nameof(images));
        lock (NativeLock)
        {
            using var _ = Invariant();
            var doc = new Document();
            try
            {
                foreach (var path in images)
                {
                    var pix = new Pixmap(path);
                    bool landscape = pix.Width > pix.Height;
                    float pw = landscape ? A4Long : A4Short, ph = landscape ? A4Short : A4Long;
                    var page = doc.NewPage(width: pw, height: ph);
                    // Cabe en la hoja completa, centrada y sin deformarse (keepProportion).
                    page.InsertImage(new MuPDF.NET.Rect(0, 0, pw, ph), filename: path, keepProportion: true);
                }
                var tmp = destPath + ".tmp";
                doc.Save(tmp);
                File.Move(tmp, destPath, overwrite: true);
            }
            finally { doc.Close(); }
        }
    }
}
