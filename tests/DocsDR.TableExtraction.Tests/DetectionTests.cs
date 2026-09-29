using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;

namespace DocsDR.TableExtraction.Tests;

public class DetectionTests
{
    private static readonly string[][] Sample =
    [
        ["Fecha", "Valor", "Descripcion"],
        ["05/03/2024", "1.234,56", "Pago proveedor"],
        ["06/03/2024", "(250,00)", "Nota credito"],
        ["07/03/2024", "10.000,00", "Deposito"],
    ];

    private static List<CleanTable> Convert(string path, out List<TableDefinition> defs)
    {
        using var doc = new MuPdfService().Open(path);
        defs = ConversionPipeline.DetectAll(doc);
        return ConversionPipeline.Build(doc, defs);
    }

    [Fact]
    public void Detects_bordered_table_as_lattice()
    {
        var path = PdfFactory.Create("lattice", d =>
        {
            var page = d.NewPage(width: 595, height: 842);
            page.InsertText(new Point(60, 60), "Estado de cuenta", fontSize: 14);
            PdfFactory.DrawTable(page, 100, Sample, borders: true);
        });

        var tables = Convert(path, out var defs);

        var def = Assert.Single(defs);
        Assert.Equal(DetectionMethod.Lattice, def.Method);
        var t = Assert.Single(tables);
        Assert.Equal(["Fecha", "Valor", "Descripcion"], t.Headers);
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal(ColumnType.Date, t.Types[0]);
        Assert.Equal(ColumnType.Number, t.Types[1]);
        Assert.Equal(1234.56m, t.Rows[0][1].Value);
        Assert.Equal(-250m, t.Rows[1][1].Value);
        Assert.Equal(new DateTime(2024, 3, 5), t.Rows[0][0].Value);
        Assert.Equal("Pago proveedor", t.Rows[0][2].Value);
    }

    [Fact]
    public void Detects_borderless_table_and_ignores_title()
    {
        var path = PdfFactory.Create("stream", d =>
        {
            var page = d.NewPage(width: 595, height: 842);
            page.InsertText(new Point(60, 60), "Reporte de movimientos del mes de marzo", fontSize: 12);
            PdfFactory.DrawTable(page, 100, Sample, borders: false);
        });

        var tables = Convert(path, out var defs);

        var def = Assert.Single(defs);
        Assert.Equal(DetectionMethod.Stream, def.Method);
        Assert.Equal(3, def.ColumnCount);
        var t = Assert.Single(tables);
        Assert.Equal("Fecha", t.Headers[0]);
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal(10000m, t.Rows[2][1].Value);
    }

    [Fact]
    public void Merges_table_across_pages_and_drops_repeated_header()
    {
        var path = PdfFactory.Create("multipage", d =>
        {
            var p1 = d.NewPage(width: 595, height: 842);
            PdfFactory.DrawTable(p1, 100, Sample, borders: true);
            var p2 = d.NewPage(width: 595, height: 842);
            PdfFactory.DrawTable(p2, 60, [Sample[0], ["08/03/2024", "99,90", "Comision"]], borders: true);
        });

        var tables = Convert(path, out _);

        var t = Assert.Single(tables);
        Assert.Equal([1, 2], t.SourcePages);
        Assert.Equal(4, t.Rows.Count);
        Assert.Equal(99.90m, t.Rows[3][1].Value);
    }

    [Fact]
    public void Manual_region_suggests_columns()
    {
        var path = PdfFactory.Create("manual", d =>
            PdfFactory.DrawTable(d.NewPage(width: 595, height: 842), 100, Sample, borders: false));

        using var doc = new MuPdfService().Open(path);
        var content = doc.GetPageContent(0);
        var def = TableDetector.FromUserRegion(content, new PdfRect(55, 95, 400, 185));

        Assert.Equal(DetectionMethod.Manual, def.Method);
        Assert.Equal(3, def.ColumnCount);
        var grid = GridBuilder.Build(def, content.Words);
        Assert.Equal(4, grid.Rows.Count);
        Assert.Equal("Deposito", grid.Rows[3][2]);
    }

    [Fact]
    public void Detects_lines_in_scanned_image()
    {
        // Imagen sintética a 2 px/pt: rejilla de 3x2 celdas.
        const int w = 400, h = 200;
        var px = Enumerable.Repeat((byte)255, w * h).ToArray();
        foreach (int y in new[] { 20, 80, 140 })
            for (int x = 20; x <= 380; x++) { px[y * w + x] = 0; px[(y + 1) * w + x] = 0; }
        foreach (int x in new[] { 20, 140, 260, 380 })
            for (int y = 20; y <= 141; y++) px[y * w + x] = 0;

        var lines = ImageLineDetector.Detect(new GrayImage(w, h, px, 2.0));

        Assert.Equal(3, lines.Count(l => l.IsHorizontal));
        Assert.Equal(4, lines.Count(l => l.IsVertical));
        var words = new List<TextWord> { new("A", new PdfRect(20, 20, 30, 30)), new("B", new PdfRect(80, 50, 90, 60)) };
        var table = Assert.Single(LatticeDetector.Detect(lines, words));
        Assert.Equal(3, table.ColumnCount);
        Assert.Single(table.RowSeparators);
    }
}
