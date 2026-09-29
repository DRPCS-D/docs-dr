using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;

namespace DocsDR.TableExtraction.Tests;

public class EditingTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>PDF de <paramref name="pages"/> páginas; la página i tiene ancho 200+i*10 para poder identificarla.</summary>
    private static string CreatePdf(int pages, string name = "edit")
    {
        return PdfFactory.Create(name, d =>
        {
            for (int i = 0; i < pages; i++)
            {
                var p = d.NewPage(width: 200 + i * 10, height: 300);
                p.InsertText(new Point(20, 50), $"Pagina {i + 1} texto de prueba", fontSize: 12);
            }
        });
    }

    private static (IPdfDocument Doc, IPdfEditor Editor) Open(string path)
    {
        var doc = new MuPdfService().Open(path);
        return (doc, Assert.IsAssignableFrom<IPdfEditor>(doc));
    }

    private static double[] Widths(IPdfDocument doc) =>
        Enumerable.Range(0, doc.PageCount).Select(i => doc.GetPageSize(i).Width).ToArray();

    [Fact]
    public void Adds_every_annotation_kind_and_they_survive_save_and_reopen()
    {
        var path = CreatePdf(1);
        var (doc, ed) = Open(path);
        var words = doc.GetWords(0);

        ed.AddTextMarkup(0, AnnotationKind.Highlight, [words[0].Box], PdfColor.Yellow, "Yo");
        ed.AddTextMarkup(0, AnnotationKind.Underline, [words[1].Box], PdfColor.Blue, "Yo");
        ed.AddTextMarkup(0, AnnotationKind.StrikeOut, [words[2].Box], PdfColor.Red, "Yo");
        ed.AddNote(0, new PdfPoint(150, 100), "Mi nota", PdfColor.Orange, "Yo");
        ed.AddFreeText(0, new PdfRect(20, 120, 180, 150), "Texto libre", 12, PdfColor.Blue, "Yo");
        ed.AddInk(0, [new(20, 200), new(60, 230), new(100, 200)], PdfColor.Red, 2, "Yo");
        ed.AddShape(0, AnnotationKind.Rectangle, new(20, 240), new(80, 280), PdfColor.Green, 2, "Yo");
        ed.AddShape(0, AnnotationKind.Ellipse, new(100, 240), new(160, 280), PdfColor.Green, 2, "Yo");
        ed.AddShape(0, AnnotationKind.Line, new(20, 290), new(100, 290), PdfColor.Black, 1, "Yo");
        ed.AddShape(0, AnnotationKind.Arrow, new(110, 290), new(190, 290), PdfColor.Black, 1, "Yo");
        ed.AddStamp(0, new PdfRect(140, 20, 190, 45), Png, "Yo");

        var expected = new[]
        {
            AnnotationKind.Highlight, AnnotationKind.Underline, AnnotationKind.StrikeOut, AnnotationKind.Note,
            AnnotationKind.FreeText, AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse,
            AnnotationKind.Line, AnnotationKind.Arrow, AnnotationKind.Stamp,
        };
        Assert.Equal(expected, ed.GetAnnotations(0).Select(a => a.Kind).ToArray());

        ed.Save(path); // guarda encima del archivo abierto
        doc.Dispose();

        var (doc2, ed2) = Open(path);
        var annots = ed2.GetAnnotations(0);
        Assert.Equal(expected, annots.Select(a => a.Kind).ToArray());
        Assert.Equal("Yo", annots[0].Author);
        Assert.Equal("Mi nota", annots.Single(a => a.Kind == AnnotationKind.Note).Content);
        Assert.Equal(PdfColor.Yellow.R, annots[0].Color!.Value.R);
        doc2.Dispose();
    }

    [Fact]
    public void Updates_moves_and_deletes_annotations()
    {
        var (doc, ed) = Open(CreatePdf(1));
        int note = ed.AddNote(0, new PdfPoint(100, 100), "uno", PdfColor.Yellow, "Yo");
        int rect = ed.AddShape(0, AnnotationKind.Rectangle, new(20, 200), new(80, 250), PdfColor.Green, 2, "Yo");

        ed.UpdateAnnotation(0, note, "dos", PdfColor.Red);
        var updated = ed.GetAnnotations(0).Single(a => a.Id == note);
        Assert.Equal("dos", updated.Content);
        Assert.InRange((int)updated.Color!.Value.R, PdfColor.Red.R - 2, PdfColor.Red.R + 2);
        Assert.InRange((int)updated.Color!.Value.G, PdfColor.Red.G - 2, PdfColor.Red.G + 2);

        var before = ed.GetAnnotations(0).Single(a => a.Id == rect).Box;
        ed.MoveAnnotation(0, rect, 30, -20);
        var after = ed.GetAnnotations(0).Single(a => a.Id == rect).Box;
        Assert.InRange(after.X0, before.X0 + 28, before.X0 + 32);
        Assert.InRange(after.Y0, before.Y0 - 22, before.Y0 - 18);

        ed.DeleteAnnotation(0, note);
        Assert.Single(ed.GetAnnotations(0));
        doc.Dispose();
    }

    [Fact]
    public void Annotations_are_drawn_in_the_render()
    {
        var (doc, ed) = Open(CreatePdf(1));
        var before = doc.Render(0, 1).Pixels.ToArray();
        ed.AddShape(0, AnnotationKind.Rectangle, new(20, 100), new(120, 200), PdfColor.Red, 3, "Yo");
        Assert.False(before.SequenceEqual(doc.Render(0, 1).Pixels));
        doc.Dispose();
    }

    [Fact]
    public void Page_operations_rotate_delete_reorder_insert()
    {
        var (doc, ed) = Open(CreatePdf(4)); // anchos 200, 210, 220, 230

        ed.RotatePages([1], 90);
        Assert.Equal([200, 300, 220, 230], Widths(doc)); // la página 2 (210x300) rotada = 300x210

        ed.DeletePages([0, 2]);
        Assert.Equal([300, 230], Widths(doc));

        ed.ReorderPages([1, 0]);
        Assert.Equal([230, 300], Widths(doc));

        ed.InsertBlankPage(1, 111, 222);
        Assert.Equal([230, 111, 300], Widths(doc));

        Assert.Throws<InvalidOperationException>(() => ed.DeletePages([0, 1, 2]));
        doc.Dispose();
    }

    [Fact]
    public void Inserts_pages_from_another_pdf_and_extracts_pages()
    {
        var (doc, ed) = Open(CreatePdf(2, "main"));
        var other = CreatePdf(3, "other"); // anchos 200, 210, 220

        Assert.Equal(3, ed.InsertPagesFrom(other, 1));
        Assert.Equal([200, 200, 210, 220, 210], Widths(doc));

        var dest = PdfFactory.TempPath("extract");
        ed.ExtractPages([4, 2, 3], dest); // orden pedido: 210, 210, 220
        using var extracted = new MuPdfService().Open(dest);
        Assert.Equal([210, 210, 220], Widths(extracted));
        Assert.Equal(5, doc.PageCount); // el original no cambia
        doc.Dispose();
    }

    [Fact]
    public void Snapshots_restore_previous_state()
    {
        var (doc, ed) = Open(CreatePdf(2));
        var snap = ed.CreateSnapshot();

        ed.AddNote(0, new PdfPoint(50, 50), "x", PdfColor.Yellow, "Yo");
        ed.DeletePages([1]);
        Assert.Equal(1, doc.PageCount);
        Assert.Single(ed.GetAnnotations(0));

        ed.RestoreSnapshot(snap);

        Assert.Equal(2, doc.PageCount);
        Assert.Empty(ed.GetAnnotations(0));
        Assert.NotEmpty(doc.GetWords(0)); // el contenido en caché se reconstruyó
        doc.Dispose();
    }

    /// <summary>PDF con contraseña de usuario "clave" (cifrado AES).</summary>
    private static string CreateEncryptedPdf()
    {
        var path = PdfFactory.TempPath("cifrado");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (DocsDR.Pdf.MuPdfDocument.NativeLock)
        {
            var d = new Document();
            d.NewPage(width: 200, height: 200).InsertText(new Point(20, 50), "secreto", fontSize: 12);
            File.WriteAllBytes(path, d.Write(encryption: 5, permissions: 4095, ownerPW: "dueno", userPW: "clave"));
            d.Close();
        }
        return path;
    }

    [Fact]
    public void Password_protected_pdf_opens_only_with_the_right_password()
    {
        var path = CreateEncryptedPdf();

        Assert.Throws<PdfPasswordRequiredException>(() => new MuPdfService().Open(path));
        Assert.Throws<PdfPasswordRequiredException>(() => new MuPdfService().Open(path, "incorrecta"));

        using var doc = new MuPdfService().Open(path, "clave");
        Assert.Equal(1, doc.PageCount);
        Assert.Contains(doc.GetWords(0), w => w.Text == "secreto");
    }

    [Fact]
    public void Editing_and_saving_a_protected_pdf_keeps_its_password()
    {
        var path = CreateEncryptedPdf();
        var doc = new MuPdfService().Open(path, "clave");
        var ed = Assert.IsAssignableFrom<IPdfEditor>(doc);
        ed.AddNote(0, new PdfPoint(50, 50), "n", PdfColor.Yellow, "Yo");
        ed.RestoreSnapshot(ed.CreateSnapshot()); // deshacer/rehacer también funciona con documentos cifrados
        ed.Save(path);
        doc.Dispose();

        Assert.Throws<PdfPasswordRequiredException>(() => new MuPdfService().Open(path)); // sigue protegido
        using var reopened = new MuPdfService().Open(path, "clave");
        Assert.Single(((IPdfEditor)reopened).GetAnnotations(0));
    }

    [Fact]
    public void Merges_several_files_in_order()
    {
        var a = CreatePdf(2, "a");   // anchos 200, 210
        var b = CreatePdf(1, "b");   // 200
        var dest = PdfFactory.TempPath("merged");

        new MuPdfService().Merge([b, a], dest);

        using var merged = new MuPdfService().Open(dest);
        Assert.Equal([200, 200, 210], Widths(merged));
    }

    [Fact]
    public void Save_as_changes_file_path_and_original_file_stays_unlocked()
    {
        var path = CreatePdf(1);
        var (doc, ed) = Open(path);
        ed.AddNote(0, new PdfPoint(10, 10), "n", PdfColor.Yellow, "Yo");
        var copy = PdfFactory.TempPath("copy");

        ed.Save(copy);

        Assert.Equal(copy, doc.FilePath);
        File.Delete(path); // el original no está bloqueado porque se cargó en memoria
        Assert.True(File.Exists(copy));
        doc.Dispose();
    }
}
