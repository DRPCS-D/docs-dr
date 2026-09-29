using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>Flujo completo: definiciones por página → celdas → unión multipágina → limpieza.</summary>
public static class ConversionPipeline
{
    public static List<TableDefinition> DetectAll(IPdfDocument doc, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var defs = new List<TableDefinition>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            defs.AddRange(TableDetector.DetectPage(doc.GetPageContent(p)));
            progress?.Report(p + 1);
        }
        return defs;
    }

    public static List<CleanTable> Build(IPdfDocument doc, IEnumerable<TableDefinition> definitions, CleanOptions? options = null)
    {
        var grids = definitions
            .Select(d => GridBuilder.Build(d, doc.GetPageContent(d.PageIndex).Words))
            .Where(g => g.Rows.Count > 0)
            .ToList();
        return MultiPageMerger.Merge(grids).Select(t => DataCleaner.Clean(t, options)).ToList();
    }
}
