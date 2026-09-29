using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>
/// Detecta tablas sin bordes: bloques de renglones consecutivos con varias "columnas" de texto
/// alineadas, cuyas separaciones se ubican por huecos en la proyección horizontal.
/// </summary>
public static class StreamDetector
{
    public static List<TableDefinition> Detect(IReadOnlyList<TextWord> words, IEnumerable<PdfRect> exclude)
    {
        var excluded = exclude.ToList();
        var free = words.Where(w => !excluded.Any(r => r.Inflate(2).ContainsCenterOf(w.Box))).ToList();
        if (free.Count < 6) return [];

        double h = TextLines.MedianHeight(free);
        double segGap = Math.Max(h * 1.0, 5);
        var lines = TextLines.Group(free);

        var result = new List<TableDefinition>();
        foreach (var run in FindRuns(lines, segGap, h))
        {
            var seps = ComputeColumns(run, segGap);
            if (seps.Count == 0) continue;
            if (LooksLikeTextLayout(run, seps)) continue;

            var region = run.Select(l => l.Box).Aggregate((a, b) => a.Union(b)).Inflate(2);
            int cols = seps.Count + 1;
            double consistency = run.Count(l => CountColumns(l, seps) == cols) / (double)run.Count;
            result.Add(new TableDefinition
            {
                Region = region,
                ColumnSeparators = seps,
                Method = DetectionMethod.Stream,
                Confidence = Math.Round(0.5 + 0.45 * consistency, 2),
            });
        }
        return result;
    }

    /// <summary>Calcula separadores de columna para todo el texto de una región (definición manual).</summary>
    public static List<double> ColumnsInRegion(IReadOnlyList<TextWord> words, PdfRect region)
    {
        var inside = words.Where(w => region.ContainsCenterOf(w.Box)).ToList();
        if (inside.Count == 0) return [];
        double h = TextLines.MedianHeight(inside);
        return ComputeColumns(TextLines.Group(inside), Math.Max(h * 0.8, 4));
    }

    /// <summary>Secuencias de renglones con ≥2 segmentos; tolera un renglón de un solo segmento (texto partido).</summary>
    private static IEnumerable<List<TextLine>> FindRuns(List<TextLine> lines, double segGap, double h)
    {
        var run = new List<TextLine>();
        int multi = 0, pendingSingles = 0;

        IEnumerable<List<TextLine>> Flush()
        {
            // Descarta renglones sueltos al final del bloque.
            if (pendingSingles > 0) run.RemoveRange(run.Count - pendingSingles, pendingSingles);
            if (multi >= 3) yield return run;
            run = [];
            multi = 0;
            pendingSingles = 0;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            bool isMulti = line.Segments(segGap).Count >= 2;
            bool farFromPrev = run.Count > 0 && line.Box.Y0 - run[^1].Box.Y1 > h * 2.5;
            if (farFromPrev)
                foreach (var r in Flush()) yield return r;

            if (isMulti)
            {
                run.Add(line);
                multi++;
                pendingSingles = 0;
            }
            else if (run.Count > 0 && pendingSingles < 1)
            {
                run.Add(line);
                pendingSingles++;
            }
            else
            {
                foreach (var r in Flush()) yield return r;
            }
        }
        foreach (var r in Flush()) yield return r;
    }

    /// <summary>Separadores en el centro de los huecos verticales (sin texto) compartidos por los renglones.</summary>
    internal static List<double> ComputeColumns(List<TextLine> lines, double minGap)
    {
        if (lines.Count == 0) return [];
        double x0 = lines.Min(l => l.Box.X0), x1 = lines.Max(l => l.Box.X1);
        int width = (int)Math.Ceiling(x1 - x0) + 1;
        var coverage = new int[width];
        foreach (var w in lines.SelectMany(l => l.Words))
        {
            int a = Math.Clamp((int)(w.Box.X0 - x0), 0, width - 1);
            int b = Math.Clamp((int)Math.Ceiling(w.Box.X1 - x0), 0, width - 1);
            for (int i = a; i <= b; i++) coverage[i]++;
        }

        // Permite que unos pocos renglones (títulos, texto largo) crucen un hueco.
        int allowed = lines.Count >= 10 ? (int)(lines.Count * 0.05) : 0;
        var seps = new List<double>();
        int start = -1;
        for (int i = 0; i < width; i++)
        {
            bool gap = coverage[i] <= allowed;
            if (gap && start < 0) start = i;
            if ((!gap || i == width - 1) && start >= 0)
            {
                int end = gap ? i : i - 1;
                if (start > 0 && end < width - 1 && end - start + 1 >= minGap * 0.6)
                    seps.Add(x0 + (start + end) / 2.0);
                start = -1;
            }
        }
        return seps;
    }

    private static int CountColumns(TextLine line, List<double> seps) =>
        line.Words.Select(w => seps.Count(s => s < w.Box.CenterX)).Distinct().Count();

    /// <summary>Dos columnas de texto corrido (diseño de página a 2 columnas) no es una tabla.</summary>
    private static bool LooksLikeTextLayout(List<TextLine> run, List<double> seps)
    {
        if (seps.Count != 1) return false;
        double avgChars = run.SelectMany(l => l.Words).Sum(w => w.Text.Length + 1) / (double)(run.Count * 2);
        return avgChars > 35;
    }
}
