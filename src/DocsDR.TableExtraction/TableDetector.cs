using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>Orquesta la detección automática por página y las sugerencias para la definición manual.</summary>
public static class TableDetector
{
    public static IReadOnlyList<LineSegment> LinesOf(PageContent page) =>
        page.IsScanned && page.ScanImage is not null ? ImageLineDetector.Detect(page.ScanImage) : page.Lines;

    public static List<TableDefinition> DetectPage(PageContent page)
    {
        var lines = LinesOf(page);
        var lattice = LatticeDetector.Detect(lines, page.Words);
        var stream = StreamDetector.Detect(page.Words, lattice.Select(t => t.Region));

        var all = lattice.Concat(stream).OrderBy(t => t.Region.Y0).ToList();
        foreach (var t in all) t.PageIndex = page.PageIndex;
        return all;
    }

    /// <summary>Crea una definición a partir de un rectángulo dibujado por el usuario, sugiriendo columnas.</summary>
    public static TableDefinition FromUserRegion(PageContent page, PdfRect region)
    {
        var grid = LatticeDetector.DetectInRegion(LinesOf(page), page.Words, region);
        return new TableDefinition
        {
            PageIndex = page.PageIndex,
            Region = region,
            ColumnSeparators = grid?.ColumnSeparators ?? StreamDetector.ColumnsInRegion(page.Words, region),
            RowSeparators = grid?.RowSeparators ?? [],
            Method = DetectionMethod.Manual,
        };
    }

    /// <summary>Copia una definición a otras páginas (mismo formato de documento).</summary>
    public static IEnumerable<TableDefinition> ApplyToPages(TableDefinition source, IEnumerable<int> pages) =>
        pages.Where(p => p != source.PageIndex).Select(p =>
        {
            var copy = source.Clone();
            copy.PageIndex = p;
            copy.Method = DetectionMethod.Manual;
            return copy;
        });

    /// <summary>Filas automáticas convertidas en separadores explícitos (para editar filas a mano).</summary>
    public static List<double> AutoRowSeparators(TableDefinition def, IReadOnlyList<TextWord> words)
    {
        var lines = TextLines.Group(words.Where(w => def.Region.ContainsCenterOf(w.Box)));
        var seps = new List<double>();
        for (int i = 1; i < lines.Count; i++)
            seps.Add((lines[i - 1].Box.Y1 + lines[i].Box.Y0) / 2);
        return seps;
    }
}
