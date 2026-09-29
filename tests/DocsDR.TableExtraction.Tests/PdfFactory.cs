using MuPDF.NET;

namespace DocsDR.TableExtraction.Tests;

/// <summary>Genera PDFs de prueba con MuPDF para no depender de archivos binarios en el repo.</summary>
internal static class PdfFactory
{
    public const float Left = 60, RowHeight = 20;
    public static readonly float[] ColumnWidths = [120, 90, 110];

    public static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), "docsdr-tests", $"{name}-{Guid.NewGuid():N}.pdf");

    /// <summary>Dibuja una tabla; con <paramref name="borders"/> agrega líneas de rejilla.</summary>
    public static void DrawTable(Page page, float top, IReadOnlyList<string[]> rows, bool borders)
    {
        float width = ColumnWidths.Sum();
        for (int r = 0; r < rows.Count; r++)
        {
            float x = Left;
            for (int c = 0; c < rows[r].Length; c++)
            {
                page.InsertText(new Point(x + 4, top + r * RowHeight + 14), rows[r][c], fontSize: 10);
                x += ColumnWidths[c];
            }
        }
        if (!borders) return;
        for (int r = 0; r <= rows.Count; r++)
            page.DrawLine(new Point(Left, top + r * RowHeight), new Point(Left + width, top + r * RowHeight));
        float lx = Left;
        for (int c = 0; c <= ColumnWidths.Length; c++)
        {
            page.DrawLine(new Point(lx, top), new Point(lx, top + rows.Count * RowHeight));
            if (c < ColumnWidths.Length) lx += ColumnWidths[c];
        }
    }

    public static string Create(string name, Action<Document> build)
    {
        var path = TempPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (DocsDR.Pdf.MuPdfDocument.NativeLock)
        {
            var doc = new Document();
            build(doc);
            doc.Save(path);
            doc.Close();
        }
        return path;
    }
}
