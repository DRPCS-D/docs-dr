using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>Une tablas que continúan en la página siguiente y arma encabezados.</summary>
public static class MultiPageMerger
{
    private const double SeparatorTolerance = 15;

    /// <param name="grids">Tablas de todas las páginas; se ordenan por página y posición.</param>
    public static List<RawTable> Merge(IEnumerable<TableGrid> grids)
    {
        var ordered = grids.OrderBy(g => g.Definition.PageIndex).ThenBy(g => g.Definition.Region.Y0).ToList();
        var result = new List<RawTable>();
        TableGrid? prev = null;
        RawTable? current = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var g = ordered[i];
            bool firstOnPage = i == 0 || ordered[i - 1].Definition.PageIndex != g.Definition.PageIndex;
            bool prevLastOnPage = prev is not null && g.Definition.PageIndex != prev.Definition.PageIndex;

            if (current is not null && prev is not null && firstOnPage && prevLastOnPage && IsContinuation(prev, g))
            {
                var rows = g.Rows.AsEnumerable();
                // Quita encabezados repetidos al inicio de la página de continuación.
                int skip = 0;
                while (skip < g.Rows.Count && current.Headers.Count > 0 && IsHeaderRepeat(g.Rows[skip], current))
                    skip++;
                current.Rows.AddRange(rows.Skip(skip));
                if (!current.SourcePages.Contains(g.Definition.PageIndex + 1))
                    current.SourcePages.Add(g.Definition.PageIndex + 1);
            }
            else
            {
                current = Start(g);
                result.Add(current);
            }
            prev = g;
        }

        for (int i = 0; i < result.Count; i++)
        {
            var pages = result[i].SourcePages;
            var range = pages.Count == 1 ? $"pág. {pages[0]}" : $"pág. {pages.Min()}-{pages.Max()}";
            result[i].Name = $"Tabla {i + 1} ({range})";
        }
        return result;
    }

    private static RawTable Start(TableGrid g)
    {
        int headerRows = Math.Min(g.Definition.HeaderRows, g.Rows.Count);
        var table = new RawTable { SourcePages = [g.Definition.PageIndex + 1] };
        if (headerRows > 0)
        {
            table.Headers = Enumerable.Range(0, g.ColumnCount)
                .Select(c => string.Join(" ", g.Rows.Take(headerRows).Select(r => r[c]).Where(s => s.Length > 0)))
                .Select((h, c) => h.Length > 0 ? h : $"Columna {c + 1}")
                .ToList();
        }
        else
        {
            table.Headers = Enumerable.Range(1, g.ColumnCount).Select(c => $"Columna {c}").ToList();
        }
        table.Rows.AddRange(g.Rows.Skip(headerRows));
        return table;
    }

    private static bool IsContinuation(TableGrid prev, TableGrid next)
    {
        var d = next.Definition;
        if (d.ContinuesPrevious is bool forced) return forced && prev.ColumnCount == next.ColumnCount;
        if (d.PageIndex != prev.Definition.PageIndex + 1) return false;
        if (prev.ColumnCount != next.ColumnCount) return false;
        var a = prev.Definition.ColumnSeparators.OrderBy(x => x).ToList();
        var b = d.ColumnSeparators.OrderBy(x => x).ToList();
        return a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= SeparatorTolerance);
    }

    internal static string Normalize(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool IsHeaderRepeat(string[] row, RawTable table)
    {
        var joined = Normalize(string.Join(" ", row.Where(c => c.Length > 0)));
        var header = Normalize(string.Join(" ", table.Headers.Where(h => !h.StartsWith("Columna "))));
        return joined.Length > 0 && joined == header;
    }
}
