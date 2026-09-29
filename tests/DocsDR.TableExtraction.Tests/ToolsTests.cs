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

    private static byte[] SolidColorPng(byte r, byte g, byte b)
    {
        lock (MuPdfDocument.NativeLock)
        {
            var pix = new Pixmap(new Colorspace(Utils.CS_RGB), 20, 20, new byte[20 * 20 * 3], false);
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 20; x++)
                    pix.SetPixel(x, y, [r, g, b]);
            return pix.ToBytes("png");
        }
    }

    [Theory]
    [InlineData("move")]
    [InlineData("delete")]
    public void Moving_or_deleting_an_image_keeps_the_other_images_that_touch_its_box(string operation)
    {
        var green = SolidColorPng(0, 200, 0);
        var blue = SolidColorPng(0, 0, 220);
        // A (azul) y B (verde) se solapan: B cubre la parte derecha de A.
        var path = Create(p =>
        {
            p.InsertImage(new MuPDF.NET.Rect(100, 150, 200, 200), stream: blue, keepProportion: false);
            p.InsertImage(new MuPDF.NET.Rect(180, 160, 280, 210), stream: green, keepProportion: false);
        });
        var (doc, ed) = Open(path);
        var a = ed.GetImages(0).Single(i => Math.Abs(i.Box.X0 - 100) < 1);

        if (operation == "move") ed.MoveImage(a, new PdfRect(10, 10, 110, 60));
        else ed.DeleteImage(a);

        var images = ed.GetImages(0);
        Assert.Contains(images, i => Math.Abs(i.Box.X0 - 180) < 1.5 && Math.Abs(i.Box.Y0 - 160) < 1.5); // B sigue en su sitio
        Assert.Equal(operation == "move" ? 2 : 1, images.Count);
        var r = doc.Render(0, 1);
        int Pixel(int x, int y, int c) => r.Pixels[y * r.Stride + x * 3 + c];
        Assert.True(Pixel(240, 185, 1) > 150 && Pixel(240, 185, 0) < 60, "B (verde) debe seguir visible");
        Assert.True(Pixel(120, 170, 0) > 240 && Pixel(120, 170, 1) > 240, "el sitio de A debe quedar vacío");
        if (operation == "move") Assert.True(Pixel(50, 30, 2) > 150 && Pixel(50, 30, 0) < 60, "A (azul) debe estar en su nueva posición");
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
    public void Ocr_end_to_end_recognizes_a_scanned_page_and_makes_it_searchable()
    {
        // Se necesitan los datos de idioma (scripts/get-tessdata.ps1); sin ellos la prueba no aplica.
        if (TessdataLocator.Find() is null) return;

        // «Escaneado»: una página con texto grande, convertida en imagen y guardada como PDF de solo imagen.
        var text = Create(p =>
        {
            p.InsertText(new Point(40, 100), "Factura numero cuarenta", fontSize: 22, fontName: "helv");
            p.InsertText(new Point(40, 140), "Cliente almacen general", fontSize: 22, fontName: "helv");
        });
        var src = new MuPdfService().Open(text);
        var r = src.Render(0, 200 / 72.0);
        var raw = new byte[r.Width * r.Height * 3];
        for (int y = 0; y < r.Height; y++) Buffer.BlockCopy(r.Pixels, y * r.Stride, raw, y * r.Width * 3, r.Width * 3);
        var dir = Path.GetDirectoryName(PdfFactory.TempPath("ocr"))!;
        var png = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
        lock (MuPdfDocument.NativeLock) new Pixmap(new Colorspace(Utils.CS_RGB), r.Width, r.Height, raw, false).Save(png);
        var scan = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".pdf");
        new MuPdfService().ImagesToPdf([png], scan);

        var (doc, ed) = Open(scan);
        Assert.Empty(doc.GetWords(0));
        Assert.True(ed.NeedsOcr(0));
        var words = ed.RecognizePage(0)!;
        ed.AddInvisibleText(0, words);

        Assert.NotEmpty(doc.Search("cuarenta"));
        Assert.NotEmpty(doc.Search("almacen"));
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
