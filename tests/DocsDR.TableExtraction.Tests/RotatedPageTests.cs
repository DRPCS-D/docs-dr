using System.Globalization;
using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;
using TextAlign = DocsDR.Core.TextAlign;

namespace DocsDR.TableExtraction.Tests;

/// <summary>
/// Páginas con /Rotate: MuPDF lee y escribe en el espacio sin girar pero renderiza girado. La app trabaja siempre en
/// coordenadas de la vista; estas pruebas comprueban que lo que se lee, se busca y se escribe cae donde se VE.
/// </summary>
public class RotatedPageTests : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public RotatedPageTests() => CultureInfo.CurrentCulture = new CultureInfo("es-ES");

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    /// <summary>Un PNG de 2×1 píxeles: izquierda roja, derecha azul (sirve para ver la orientación de una imagen).</summary>
    private static byte[] RedBluePng()
    {
        lock (MuPdfDocument.NativeLock)
        {
            var pix = new Pixmap(new Colorspace(Utils.CS_RGB), 40, 20, new byte[40 * 20 * 3], false);
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 40; x++)
                    pix.SetPixel(x, y, x < 20 ? [255, 0, 0] : [0, 0, 255]);
            return pix.ToBytes("png");
        }
    }

    /// <summary>
    /// Página de 400×300 con /Rotate=<paramref name="rotation"/> y el texto escrito para que se vea derecho en la vista
    /// (o de lado si <paramref name="upright"/> es false, como un documento girado a mano).
    /// </summary>
    private static string Create(int rotation, bool upright = true, Action<Page, Func<double, double, Point>>? extra = null)
    {
        var path = PdfFactory.TempPath("rot");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var culture = CultureInfo.CurrentCulture;
        lock (MuPdfDocument.NativeLock)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var d = new Document();
                d.NewPage(width: 400, height: 300);
                d[0].SetRotation(rotation);
                var p = d[0];
                Point ToU(double x, double y) => new Point((float)x, (float)y) * p.DerotationMatrix;
                int rotate = upright ? rotation : 0;
                p.InsertText(upright ? ToU(60, 80) : new Point(60, 80), "Texto original de prueba", fontSize: 16, fontName: "helv", rotate: rotate);
                extra?.Invoke(p, ToU);
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

    /// <summary>Caja (en coordenadas de la vista) que rodea los píxeles distintos del fondo.</summary>
    private static PdfRect InkBox(IPdfDocument doc, Func<int, int, int, bool> isInk)
    {
        var r = doc.Render(0, 1);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                int i = y * r.Stride + x * 3;
                if (!isInk(r.Pixels[i], r.Pixels[i + 1], r.Pixels[i + 2])) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? new PdfRect(0, 0, 0, 0) : new PdfRect(minX, minY, maxX + 1, maxY + 1);
    }

    private static bool Dark(int r, int g, int b) => r < 110 && g < 110 && b < 110;

    public static IEnumerable<object[]> Rotations => [[90], [180], [270]];

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Text_and_search_are_reported_where_they_are_seen(int rotation)
    {
        var (doc, ed) = Open(Create(rotation));

        var line = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single();
        var ink = InkBox(doc, Dark);
        Assert.Equal("Texto original de prueba", line.Text);
        Assert.InRange(ink.X0, line.Box.X0 - 2, line.Box.X1);   // la tinta está dentro de la caja que se informa
        Assert.InRange(ink.Y0, line.Box.Y0 - 2, line.Box.Y1);
        Assert.InRange(ink.X1, line.Box.X0, line.Box.X1 + 2);
        Assert.InRange(ink.Y1, line.Box.Y0, line.Box.Y1 + 2);
        Assert.InRange(line.Baseline, line.Box.Y0, line.Box.Y1);

        var hit = Assert.Single(doc.Search("original"));
        Assert.True(hit.Box.X0 >= line.Box.X0 - 1 && hit.Box.X1 <= line.Box.X1 + 1 && hit.Box.Y0 >= line.Box.Y0 - 4 && hit.Box.Y1 <= line.Box.Y1 + 4);
        Assert.True(hit.Box.X0 > line.Box.X0 + 20); // «original» va después de «Texto», no al principio
        var word = doc.GetWords(0).Single(w => w.Text == "original");
        Assert.True(word.Box.X0 >= line.Box.X0 - 1 && word.Box.X1 <= line.Box.X1 + 1);
        doc.Dispose();
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Editing_a_line_on_a_rotated_page_keeps_it_in_place_and_upright(int rotation)
    {
        var (doc, ed) = Open(Create(rotation));
        var line = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single();
        var inkBefore = InkBox(doc, Dark);

        var erase = new PdfRect(line.Box.X0, Math.Max(line.Box.Y0 + 1.5, line.Baseline - 0.72 * line.Format.Size), line.Box.X1, Math.Min(line.Box.Y1 - 1.5, line.Baseline));
        var place = new PdfRect(line.Box.X0, line.Box.Y0 - 0.5, line.Box.X1 + 60, line.Box.Y1 + 4);
        ed.ReplaceText(0, erase, place, "Texto editado", line.Format, TextAlign.Left, 0, line.Baseline, [new TextRun("Texto editado", line.Format)]);

        var after = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single();
        Assert.Equal("Texto editado", after.Text); // el texto viejo se borró y el nuevo se lee derecho
        Assert.Equal(line.Baseline, after.Baseline, 0.5);
        Assert.Equal(line.Box.X0, after.Box.X0, 1.5);
        var inkAfter = InkBox(doc, Dark);
        Assert.Equal(inkBefore.X0, inkAfter.X0, 2); // empieza donde empezaba
        Assert.InRange(inkAfter.Y1, inkBefore.Y0, inkBefore.Y1 + 3);
        doc.Dispose();
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void The_textbox_path_and_new_text_also_work_on_rotated_pages(int rotation)
    {
        var (doc, ed) = Open(Create(rotation));
        var fmt = new TextFormat(TextFormat.Sans, 14, new PdfColor(0, 0, 0), false, false);

        ed.AddText(0, new PdfRect(80, 150, 300, 190), "Texto nuevo", fmt, TextAlign.Left);

        var added = ed.GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == "Texto nuevo");
        Assert.InRange(added.Box.X0, 78, 90);
        Assert.InRange(added.Box.Y0, 148, 175);
        doc.Dispose();
    }

    [Fact]
    public void Text_that_looks_sideways_is_not_offered_for_editing()
    {
        // Un documento girado a mano: el contenido no cambió, así que en la vista el texto queda de lado.
        var (doc, ed) = Open(Create(90, upright: false));

        Assert.Empty(ed.GetTextBlocks(0));
        doc.Dispose();
    }

    public static IEnumerable<object[]> AllRotations => [[0], [90], [180], [270]];

    private static bool Red(int r, int g, int b) => r > 180 && g < 90 && b < 90;

    private static void AssertNear(PdfRect actual, PdfRect expected, double tol, string what)
    {
        Assert.True(Math.Abs(actual.X0 - expected.X0) <= tol && Math.Abs(actual.Y0 - expected.Y0) <= tol
                    && Math.Abs(actual.X1 - expected.X1) <= tol && Math.Abs(actual.Y1 - expected.Y1) <= tol,
            $"{what}: se esperaba cerca de ({expected.X0:F0},{expected.Y0:F0},{expected.X1:F0},{expected.Y1:F0}) y fue ({actual.X0:F0},{actual.Y0:F0},{actual.X1:F0},{actual.Y1:F0})");
    }

    [Theory]
    [MemberData(nameof(AllRotations))]
    public void Annotations_are_drawn_reported_and_moved_where_they_are_seen(int rotation)
    {
        var red = new PdfColor(255, 0, 0);
        var target = new PdfRect(100, 50, 180, 90);

        // Cada tipo en su propio documento: qué se ve y qué caja informa la lista de anotaciones.
        void Check(string name, Action<IPdfDocument, IPdfEditor> add, PdfRect expectedInk, double tol, double listedExtra = 3)
        {
            var (doc, ed) = Open(Create(rotation));
            add(doc, ed);
            AssertNear(InkBox(doc, Red), expectedInk, tol, $"{name} (giro {rotation}) dibujada");
            var listed = ed.GetAnnotations(0).Single();
            AssertNear(listed.Box, expectedInk, tol + listedExtra, $"{name} (giro {rotation}) informada");
            doc.Dispose();
        }

        Check("rectángulo", (_, ed) => ed.AddShape(0, AnnotationKind.Rectangle, new PdfPoint(100, 50), new PdfPoint(180, 90), red, 2, "t"), target, 4);
        Check("elipse", (_, ed) => ed.AddShape(0, AnnotationKind.Ellipse, new PdfPoint(100, 50), new PdfPoint(180, 90), red, 2, "t"), target, 4);
        Check("lápiz", (_, ed) => ed.AddInk(0, [new PdfPoint(100, 50), new PdfPoint(140, 90), new PdfPoint(180, 50)], red, 3, "t"), target, 5, listedExtra: 12); // el lápiz informa su caja con margen
        Check("línea", (_, ed) => ed.AddShape(0, AnnotationKind.Line, new PdfPoint(100, 50), new PdfPoint(180, 90), red, 2, "t"), target, 5);
        Check("nota", (_, ed) => ed.AddNote(0, new PdfPoint(100, 50), "x", red, "t"), new PdfRect(100, 50, 116, 66), 5);
        Check("resaltado", (doc, ed) =>
        {
            var w = doc.GetWords(0).Single(x => x.Text == "original");
            ed.AddTextMarkup(0, AnnotationKind.Highlight, [w.Box], red, "t");
        }, doc0Word(rotation), 6);

        // Texto libre: queda dentro de la caja y se lee en horizontal.
        {
            var (doc, ed) = Open(Create(rotation));
            ed.AddFreeText(0, target, "Hola mundo", 14, red, "t");
            var ink = InkBox(doc, Red);
            Assert.True(ink.X0 >= target.X0 - 3 && ink.X1 <= target.X1 + 3 && ink.Y0 >= target.Y0 - 3 && ink.Y1 <= target.Y1 + 3, $"texto libre fuera de su caja (giro {rotation})");
            Assert.True(ink.Width > ink.Height, $"el texto libre debe verse horizontal (giro {rotation})");
            doc.Dispose();
        }

        // Mover: desplazamiento (+50, +20) en la vista.
        {
            var (doc, ed) = Open(Create(rotation));
            ed.AddShape(0, AnnotationKind.Rectangle, new PdfPoint(100, 50), new PdfPoint(180, 90), red, 2, "t");
            ed.MoveAnnotation(0, ed.GetAnnotations(0).Single().Id, 50, 20);
            AssertNear(InkBox(doc, Red), new PdfRect(150, 70, 230, 110), 4, $"rectángulo movido (giro {rotation})");
            doc.Dispose();
        }

        // Sello (imagen): en su caja y derecho (izquierda roja, derecha azul).
        {
            var (doc, ed) = Open(Create(rotation));
            ed.AddStamp(0, target, RedBluePng(), "t");
            var redBox = InkBox(doc, (r, g, b) => r > 200 && g < 60 && b < 60);
            var blueBox = InkBox(doc, (r, g, b) => b > 200 && r < 60 && g < 60);
            Assert.True(redBox.CenterX < blueBox.CenterX, $"el sello debe verse derecho (giro {rotation})");
            AssertNear(new PdfRect(Math.Min(redBox.X0, blueBox.X0), Math.Min(redBox.Y0, blueBox.Y0), Math.Max(redBox.X1, blueBox.X1), Math.Max(redBox.Y1, blueBox.Y1)), target, 5, $"sello (giro {rotation})");
            doc.Dispose();
        }
    }

    /// <summary>Caja de «original» en la vista, calculada con el propio lector (comparada luego con lo que se dibuja).</summary>
    private static PdfRect doc0Word(int rotation)
    {
        var (doc, _) = Open(Create(rotation));
        var box = doc.GetWords(0).Single(x => x.Text == "original").Box;
        doc.Dispose();
        return box;
    }

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Images_are_reported_moved_and_replaced_where_they_are_seen(int rotation)
    {
        var png = RedBluePng();
        var (doc, ed) = Open(Create(rotation));
        // Se agrega con el propio editor (coordenadas de la vista): así se prueba también AddImage.
        ed.AddImage(0, new PdfRect(100, 150, 180, 190), png);

        var image = Assert.Single(ed.GetImages(0), i => i.Box.Width > 10);
        Assert.Equal(100, image.Box.X0, 1.5);
        Assert.Equal(150, image.Box.Y0, 1.5);
        Assert.Equal(80, image.Box.Width, 1.5);

        // Orientación: el lado izquierdo de la imagen es rojo y el derecho azul, tal como se ve.
        var red = InkBox(doc, (r, g, b) => r > 200 && g < 60 && b < 60);
        var blue = InkBox(doc, (r, g, b) => b > 200 && r < 60 && g < 60);
        Assert.True(red.CenterX < blue.CenterX, "la imagen debe verse derecha, no girada");

        ed.MoveImage(image, new PdfRect(200, 60, 280, 100));
        var moved = InkBox(doc, (r, g, b) => (r > 200 && g < 60 && b < 60) || (b > 200 && r < 60 && g < 60));
        Assert.Equal(200, moved.X0, 2);
        Assert.Equal(60, moved.Y0, 2);
        doc.Dispose();
    }
}
