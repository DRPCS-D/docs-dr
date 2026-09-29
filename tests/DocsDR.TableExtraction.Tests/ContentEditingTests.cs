using System.Globalization;
using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;
using FileInfo = System.IO.FileInfo;
using TextAlign = DocsDR.Core.TextAlign;

namespace DocsDR.TableExtraction.Tests;

/// <summary>Edición del contenido existente: texto e imágenes. Se ejecutan con coma decimal (es-ES) a propósito.</summary>
public class ContentEditingTests : IDisposable
{
    private static readonly byte[] SolidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public ContentEditingTests() => CultureInfo.CurrentCulture = new CultureInfo("es-ES");

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    /// <summary>Crea un PDF de prueba. MuPDF necesita cultura invariante para escribir bien los números.</summary>
    private static string Create(Action<Page> draw)
    {
        var path = PdfFactory.TempPath("content");
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

    private static string[] Words(IPdfDocument doc) => doc.GetWords(0).Select(w => w.Text).ToArray();

    private static (int R, int G, int B) Pixel(IPdfDocument doc, int x, int y)
    {
        var r = doc.Render(0, 1);
        int i = y * r.Stride + x * 3;
        return (r.Pixels[i], r.Pixels[i + 1], r.Pixels[i + 2]);
    }

    private static string Sample() => Create(p =>
    {
        p.InsertText(new Point(30, 60), "Titulo en rojo", fontSize: 14, fontName: "hebo", color: [0.8f, 0f, 0f]);
        p.InsertText(new Point(30, 80), "Linea de arriba", fontSize: 11.5f, fontName: "tiro");
        p.InsertText(new Point(30, 94), "Linea del medio", fontSize: 11.5f, fontName: "tiro");
        p.InsertText(new Point(30, 108), "Linea de abajo", fontSize: 11.5f, fontName: "tiro");
    });

    [Fact]
    public void Reads_text_blocks_with_their_real_format()
    {
        var (doc, ed) = Open(Sample());

        var lines = ed.GetTextBlocks(0).SelectMany(b => b.Lines).ToList();

        var title = lines.Single(l => l.Text == "Titulo en rojo");
        Assert.Equal(TextFormat.Sans, title.Format.Family);
        Assert.True(title.Format.Bold);
        Assert.False(title.Format.Italic);
        Assert.Equal(14, title.Format.Size, 1);
        Assert.InRange((int)title.Format.Color.R, 0xCB, 0xCD); // 0.8 → 0xCC: se conservan los decimales
        Assert.Equal(0, title.Format.Color.G);
        var body = lines.Single(l => l.Text == "Linea del medio");
        Assert.Equal(TextFormat.Serif, body.Format.Family);
        Assert.Equal(11.5, body.Format.Size, 1);
        doc.Dispose();
    }

    [Fact]
    public void Replaces_one_line_without_touching_its_neighbours()
    {
        var (doc, ed) = Open(Sample());
        var line = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == "Linea del medio");
        var erase = new PdfRect(line.Box.X0, line.Box.Y0 + 1.5, line.Box.X1, line.Box.Y1 - 1.5);
        var place = new PdfRect(line.Box.X0, line.Box.Y0 - 0.5, line.Box.X1 + 100, line.Box.Y0 + 20);

        ed.ReplaceText(0, erase, place, "Línea nueva con acentos ñ", line.Format, TextAlign.Left);

        var words = Words(doc);
        Assert.DoesNotContain("medio", words);
        Assert.Contains("nueva", words);
        Assert.Contains("ñ", words);
        Assert.Contains("arriba", words); // vecinas intactas
        Assert.Contains("abajo", words);
        Assert.Contains("Titulo", words);
        doc.Dispose();
    }

    [Fact]
    public void New_text_keeps_the_requested_color_and_supports_characters_outside_latin1()
    {
        var (doc, ed) = Open(Sample());
        var fmt = new TextFormat(TextFormat.Sans, 12.5, new PdfColor(0, 102, 0), Bold: false, Italic: false);

        ed.AddText(0, new PdfRect(30, 200, 380, 230), "Precio: 25 € — “nuevo”", fmt, TextAlign.Left);

        var added = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text.StartsWith("Precio"));
        Assert.Contains("€", added.Text);
        Assert.InRange((int)added.Format.Color.G, 100, 104);
        Assert.Equal(0, added.Format.Color.R);
        Assert.Equal(12.5, added.Format.Size, 1);
        doc.Dispose();
    }

    [Fact]
    public void Erasing_text_keeps_the_background_and_graphics()
    {
        var path = Create(p =>
        {
            p.DrawRect(new Rect(20, 100, 380, 130), color: [0f, 0f, 0f], fill: [0.8f, 0.9f, 1f]);
            p.InsertText(new Point(30, 120), "Texto sobre fondo", fontSize: 12, fontName: "helv");
        });
        var (doc, ed) = Open(path);
        var before = Pixel(doc, 300, 115); // zona de fondo azul sin texto
        var line = ed.GetTextBlocks(0).Single().Lines.Single();

        ed.ReplaceText(0, new PdfRect(line.Box.X0, line.Box.Y0 + 1, line.Box.X1, line.Box.Y1 - 1), line.Box, "", line.Format, TextAlign.Left);

        Assert.Empty(Words(doc));
        Assert.Equal(before, Pixel(doc, 300, 115));
        Assert.Equal(before, Pixel(doc, 35, 112)); // donde estaba el texto: sigue el fondo, no un recuadro blanco
        Assert.NotEqual((255, 255, 255), before);
        doc.Dispose();
    }

    [Fact]
    public void Right_alignment_places_text_against_the_right_edge()
    {
        var (doc, ed) = Open(Sample());
        var fmt = new TextFormat(TextFormat.Sans, 12, new PdfColor(0, 0, 0), false, false);

        ed.AddText(0, new PdfRect(100, 300, 300, 320), "1.234,56", fmt, TextAlign.Right);

        var word = doc.GetWords(0).Single(w => w.Text == "1.234,56");
        Assert.InRange(word.Box.X1, 296, 300.5);
        doc.Dispose();
    }

    [Fact]
    public void Long_text_shrinks_the_font_and_impossible_text_fails_without_changing_the_page()
    {
        var (doc, ed) = Open(Sample());
        var fmt = new TextFormat(TextFormat.Sans, 12, new PdfColor(0, 0, 0), false, false);
        var narrow = new PdfRect(30, 300, 190, 316); // una sola línea de ~160 pt

        double used = ed.AddText(0, narrow, "Texto algo largo para esta caja", fmt, TextAlign.Left);
        Assert.InRange(used, 7.2, 11.9); // reducido, pero no por debajo del 60 %

        var before = Words(doc);
        Assert.Throws<InvalidOperationException>(() =>
            ed.AddText(0, new PdfRect(30, 340, 60, 356), "Este texto no cabe de ninguna manera en la caja", fmt, TextAlign.Left));
        Assert.Equal(before, Words(doc));
        doc.Dispose();
    }

    [Fact]
    public void Paragraph_text_is_rewrapped_inside_the_block()
    {
        var path = Create(p =>
        {
            p.InsertTextbox(new Rect(30, 40, 230, 120),
                "Este es un parrafo de varias lineas que ocupa un bloque completo del documento de prueba.",
                fontSize: 10, fontName: "helv");
        });
        var (doc, ed) = Open(path);
        // MuPDF puede dividir un texto escrito línea a línea en varios bloques: se trata todo como un párrafo.
        var blocks = ed.GetTextBlocks(0);
        var lines = blocks.SelectMany(b => b.Lines).ToList();
        var box = blocks.Select(b => b.Box).Aggregate((a, b) => a.Union(b));
        Assert.True(lines.Count >= 3);
        var format = lines[0].Format;

        double pitch = (lines[^1].Box.Y0 - lines[0].Box.Y0) / (lines.Count - 1);
        ed.ReplaceText(0, new PdfRect(box.X0, box.Y0 + 1, box.X1, box.Y1 - 1), box,
            "Texto reemplazado del parrafo con un largo suficiente para volver a ocupar varias lineas dentro del mismo bloque.",
            format, TextAlign.Left, lineHeightFactor: pitch / format.Size);

        var afterLines = ed.GetTextBlocks(0).SelectMany(b => b.Lines).ToList();
        var afterText = string.Join(" ", afterLines.Select(l => l.Text));
        Assert.Contains("reemplazado", afterText);
        Assert.DoesNotContain("Este es un parrafo", afterText);
        Assert.True(afterLines.Count >= 2);
        Assert.All(afterLines, l => Assert.True(l.Box.X1 <= box.X1 + 1));
        doc.Dispose();
    }

    [Fact]
    public void Text_edits_can_be_undone_with_snapshots()
    {
        var (doc, ed) = Open(Sample());
        var snapshot = ed.CreateSnapshot();
        var fmt = new TextFormat(TextFormat.Sans, 12, new PdfColor(0, 0, 0), false, false);
        ed.AddText(0, new PdfRect(30, 300, 300, 320), "Agregado", fmt, TextAlign.Left);
        Assert.Contains("Agregado", Words(doc));

        ed.RestoreSnapshot(snapshot);

        Assert.DoesNotContain("Agregado", Words(doc));
        doc.Dispose();
    }

    [Fact]
    public void Editing_text_is_refused_on_rotated_pages()
    {
        var (doc, ed) = Open(Sample());
        ed.RotatePages([0], 90);
        var fmt = new TextFormat(TextFormat.Sans, 12, new PdfColor(0, 0, 0), false, false);

        Assert.Throws<NotSupportedException>(() => ed.AddText(0, new PdfRect(30, 300, 300, 320), "x", fmt, TextAlign.Left));
        doc.Dispose();
    }

    // ---------------- Imágenes ----------------

    private static string WithImage() => Create(p =>
    {
        p.InsertText(new Point(30, 60), "Texto que debe sobrevivir", fontSize: 12, fontName: "helv");
        p.InsertImage(new Rect(100, 150, 200, 210), stream: SolidPng, keepProportion: false);
    });

    [Fact]
    public void Lists_page_images_with_their_box()
    {
        var (doc, ed) = Open(WithImage());

        var img = Assert.Single(ed.GetImages(0));

        Assert.InRange(img.Box.X0, 99, 101);
        Assert.InRange(img.Box.Y1, 209, 211);
        doc.Dispose();
    }

    [Fact]
    public void Moves_an_image_and_keeps_the_text()
    {
        var (doc, ed) = Open(WithImage());
        var img = ed.GetImages(0).Single();
        Assert.NotEqual((255, 255, 255), Pixel(doc, 150, 180));

        ed.MoveImage(img, new PdfRect(250, 250, 350, 310));

        var moved = Assert.Single(ed.GetImages(0));
        Assert.InRange(moved.Box.X0, 249, 251);
        Assert.InRange(moved.Box.Y0, 249, 251);
        Assert.Equal((255, 255, 255), Pixel(doc, 150, 180));           // el lugar anterior quedó vacío
        Assert.NotEqual((255, 255, 255), Pixel(doc, 300, 280));        // y la imagen está en el nuevo
        Assert.Contains("sobrevivir", Words(doc));
        doc.Dispose();
    }

    [Fact]
    public void Resizes_deletes_replaces_and_adds_images()
    {
        var (doc, ed) = Open(WithImage());
        var img = ed.GetImages(0).Single();

        ed.MoveImage(img, new PdfRect(100, 150, 250, 240)); // agrandar
        var resized = ed.GetImages(0).Single();
        Assert.InRange(resized.Box.Width, 149, 151);

        ed.ReplaceImage(resized, SolidPng);
        Assert.Single(ed.GetImages(0));

        ed.AddImage(0, new PdfRect(20, 300, 80, 340), SolidPng);
        Assert.Equal(2, ed.GetImages(0).Count);

        ed.DeleteImage(ed.GetImages(0).First(i => i.Box.Y0 > 290));
        var left = Assert.Single(ed.GetImages(0));
        Assert.InRange(left.Box.Y0, 149, 151);
        Assert.Contains("sobrevivir", Words(doc));
        doc.Dispose();
    }

    [Fact]
    public void Repeated_text_edits_do_not_bloat_the_file()
    {
        var path = Sample();
        var (doc, ed) = Open(path);
        var fmt = new TextFormat(TextFormat.Serif, 12, new PdfColor(0, 0, 0), false, false);
        long before = new FileInfo(path).Length;

        for (int i = 0; i < 6; i++)
            ed.AddText(0, new PdfRect(30, 200 + i * 20, 380, 218 + i * 20), $"Fila {i} con € y acentos áéí", fmt, TextAlign.Left);
        ed.Save(path);

        Assert.True(new FileInfo(path).Length < before + 900_000, $"creció de {before} a {new FileInfo(path).Length}");
        doc.Dispose();
    }
}
