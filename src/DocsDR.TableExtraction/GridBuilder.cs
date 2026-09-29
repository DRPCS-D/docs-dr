using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>Aplica una <see cref="TableDefinition"/> a las palabras de la página y produce las celdas.</summary>
public static class GridBuilder
{
    public static TableGrid Build(TableDefinition def, IReadOnlyList<TextWord> pageWords)
    {
        var words = pageWords.Where(w => def.Region.ContainsCenterOf(w.Box)).ToList();
        int cols = def.ColumnCount;
        var colSeps = def.ColumnSeparators.OrderBy(x => x).ToList();
        int Col(TextWord w) => colSeps.Count(s => s < w.Box.CenterX);

        List<List<TextWord>> rowGroups;
        if (def.RowSeparators.Count > 0)
        {
            var rowSeps = def.RowSeparators.OrderBy(y => y).ToList();
            rowGroups = Enumerable.Range(0, rowSeps.Count + 1).Select(_ => new List<TextWord>()).ToList();
            foreach (var w in words) rowGroups[rowSeps.Count(s => s < w.Box.CenterY)].Add(w);
        }
        else
        {
            rowGroups = TextLines.Group(words).Select(l => l.Words).ToList();
        }

        var rows = new List<string[]>();
        foreach (var group in rowGroups)
        {
            var cells = new string[cols];
            foreach (var cellWords in group.GroupBy(Col))
            {
                // Dentro de la celda: orden de lectura (renglón y luego X).
                var text = string.Join(" ", TextLines.Group(cellWords).SelectMany(l => l.Words).Select(w => w.Text));
                cells[cellWords.Key] = text;
            }
            for (int i = 0; i < cols; i++) cells[i] ??= "";
            if (cells.Any(c => c.Length > 0)) rows.Add(cells);
        }
        return new TableGrid(def, rows);
    }
}
