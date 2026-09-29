using System.Globalization;
using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;
using TextAlign = DocsDR.Core.TextAlign;

namespace DocsDR.TableExtraction.Tests;

/// <summary>OCR a PDF buscable, PDF desde imágenes y reemplazo de texto (motor).</summary>
public class ToolsTests : IDisposable
{
    private static readonly byte[] SolidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public ToolsTests() => CultureInfo.CurrentCulture = new CultureInfo("es-ES");

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    private static string Create(Action<Page> draw)
    {
        var path = PdfFactory.TempPath("tools");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var culture = CultureInfo.CurrentCulture;
        lock (MuPdfDocument.NativeLock)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var d = new Document();
                draw(d.NewPage(width: 400, height: 400));
                d.Save(path);
                d.Close();
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
        return path;
    }

    private static (IPdfDocument Doc, IPdfEditor Ed) Open(string path)
    {
        var doc = new MuPdfService().Open(path);
        return (doc, (IPdfEditor)doc);
    }

    [Fact]
    public void Invisible_text_makes_a_scanned_page_searchable_without_changing_how_it_looks()
    {
        // Una página que es solo una imagen (como un escaneado) no tiene texto que buscar.
        var path = Create(p => p.InsertImage(new MuPDF.NET.Rect(0, 0, 400, 400), stream: SolidPng, keepProportion: false));
        var (doc, ed) = Open(path);
        Assert.True(ed.NeedsOcr(0));
        Assert.Empty(doc.Search("Factura"));
        var before = doc.Render(0, 1).Pixels.ToArray();

        // Palabras como las devolvería el OCR.
        ed.AddInvisibleText(0, [
            new TextWord("Factura", new PdfRect(50, 60, 130, 80)), new TextWord("número", new PdfRect(140, 60, 200, 80)),
            new TextWord("cuarenta", new PdfRect(50, 100, 130, 120)), new TextWord("mil", new PdfRect(140, 100, 170, 120)),
        ]);

        var hits = doc.Search("Factura");
        var hit = Assert.Single(hits);
        Assert.InRange(hit.Box.X0, 45, 55);
        Assert.InRange(hit.Box.Y0, 55, 70);
        Assert.Contains(doc.GetWords(0), w => w.Text == "número");
        Assert.Equal(before, doc.Render(0, 1).Pixels.ToArray()); // el texto es invisible
        Assert.False(ed.NeedsOcr(0)); // ya tiene texto
        doc.Dispose();
    }

    [Fact]
    public void Ocr_reports_unavailable_data_instead_of_failing()
    {
        var path = Create(p => p.InsertText(new Point(40, 100), "Hola", fontSize: 12, fontName: "helv"));
        var (doc, ed) = Open(path);

        Assert.False(ed.NeedsOcr(0)); // una página con texto nunca se reconoce
        if (!ed.OcrAvailable) Assert.Null(ed.RecognizePage(0));
        doc.Dispose();
    }

    [Fact]
    public void Images_to_pdf_makes_one_a4_page_per_image_landscape_when_wider()
    {
        var dir = Path.GetDirectoryName(PdfFactory.TempPath("img"))!;
        Directory.CreateDirectory(dir);
        var portrait = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(portrait, SolidPng);
        var landscape = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
        lock (MuPdfDocument.NativeLock)
        {
            var pix = new Pixmap(new Colorspace(Utils.CS_RGB), 200, 100, new byte[200 * 100 * 3], false);
            pix.Save(landscape);
        }
        var dest = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".pdf");

        new MuPdfService().ImagesToPdf([portrait, landscape], dest);

        var doc = new MuPdfService().Open(dest);
        Assert.Equal(2, doc.PageCount);
        var (w1, h1) = doc.GetPageSize(0);
        var (w2, h2) = doc.GetPageSize(1);
        Assert.Equal((595, 842), (Math.Round(w1), Math.Round(h1)));
        Assert.Equal((842, 595), (Math.Round(w2), Math.Round(h2)));
        Assert.Single(((IPdfEditor)doc).GetImages(0));
        doc.Dispose();
    }

    [Fact]
    public void A_replacement_that_does_not_fit_leaves_the_original_line_untouched()
    {
        var path = Create(p => p.InsertText(new Point(40, 100), "Texto original", fontSize: 12, fontName: "helv"));
        var (doc, ed) = Open(path);
        var line = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single();
        var erase = new PdfRect(line.Box.X0, line.Box.Y0 + 1.5, line.Box.X1, line.Box.Y1 - 1.5);
        var place = new PdfRect(line.Box.X0, line.Box.Y0 - 0.5, line.Box.X1 + 10, line.Box.Y1 + 4);
        var tooLong = new string('W', 80);

        Assert.Throws<InvalidOperationException>(() =>
            ed.ReplaceText(0, erase, place, tooLong, line.Format, TextAlign.Left, 0, line.Baseline, [new TextRun(tooLong, line.Format)]));

        Assert.Equal("Texto original", ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single().Text); // no se borró nada
        doc.Dispose();
    }
}
