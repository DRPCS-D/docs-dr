using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>Detecta tablas con bordes a partir de segmentos horizontales y verticales que se cruzan.</summary>
public static class LatticeDetector
{
    private const double Tol = 3.0;
    private const double MinSegment = 8.0;

    public static List<TableDefinition> Detect(IReadOnlyList<LineSegment> lines, IReadOnlyList<TextWord> words)
    {
        var h = MergeCollinear(lines.Where(l => l.IsHorizontal && l.Length >= MinSegment), horizontal: true);
        var v = MergeCollinear(lines.Where(l => l.IsVertical && l.Length >= MinSegment), horizontal: false);
        if (h.Count < 2 || v.Count < 2) return [];

        // Union-find sobre segmentos que se intersectan.
        int n = h.Count + v.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < h.Count; i++)
            for (int j = 0; j < v.Count; j++)
                if (Intersects(h[i], v[j]))
                    parent[Find(i)] = Find(h.Count + j);

        var result = new List<TableDefinition>();
        foreach (var cluster in Enumerable.Range(0, n).GroupBy(Find))
        {
            var hs = cluster.Where(i => i < h.Count).Select(i => h[i]).ToList();
            var vs = cluster.Where(i => i >= h.Count).Select(i => v[i - h.Count]).ToList();
            if (hs.Count < 2 || vs.Count < 2) continue;

            var region = new PdfRect(
                Math.Min(hs.Min(s => s.X0), vs.Min(s => s.X0)),
                Math.Min(hs.Min(s => s.Y0), vs.Min(s => s.Y0)),
                Math.Max(hs.Max(s => s.X1), vs.Max(s => s.X1)),
                Math.Max(hs.Max(s => s.Y1), vs.Max(s => s.Y1)));

            var xs = Cluster(vs.Select(s => s.X0));
            var ys = Cluster(hs.Select(s => s.Y0));
            var colSeps = xs.Where(x => x > region.X0 + Tol && x < region.X1 - Tol).ToList();
            var rowSeps = ys.Where(y => y > region.Y0 + Tol && y < region.Y1 - Tol).ToList();

            int cols = colSeps.Count + 1, rows = rowSeps.Count + 1;
            if (rows < 2 || cols * rows < 4) continue;
            if (!words.Any(w => region.ContainsCenterOf(w.Box))) continue;

            result.Add(new TableDefinition
            {
                Region = region,
                ColumnSeparators = colSeps,
                RowSeparators = rowSeps,
                Method = DetectionMethod.Lattice,
                Confidence = 0.95,
            });
        }
        return result;
    }

    /// <summary>Busca una rejilla de líneas dentro de una región dibujada por el usuario.</summary>
    public static TableDefinition? DetectInRegion(IReadOnlyList<LineSegment> lines, IReadOnlyList<TextWord> words, PdfRect region)
    {
        var inside = lines.Where(l => region.Inflate(Tol).Contains(l.X0, l.Y0) && region.Inflate(Tol).Contains(l.X1, l.Y1)).ToList();
        return Detect(inside, words).OrderByDescending(t => t.Region.Width * t.Region.Height).FirstOrDefault();
    }

    private static bool Intersects(LineSegment h, LineSegment v) =>
        v.X0 >= h.X0 - Tol && v.X0 <= h.X1 + Tol && h.Y0 >= v.Y0 - Tol && h.Y0 <= v.Y1 + Tol;

    /// <summary>Une segmentos colineales que se tocan o solapan.</summary>
    internal static List<LineSegment> MergeCollinear(IEnumerable<LineSegment> segs, bool horizontal)
    {
        var norm = segs.Select(s => horizontal
                ? (Pos: (s.Y0 + s.Y1) / 2, Start: Math.Min(s.X0, s.X1), End: Math.Max(s.X0, s.X1))
                : (Pos: (s.X0 + s.X1) / 2, Start: Math.Min(s.Y0, s.Y1), End: Math.Max(s.Y0, s.Y1)))
            .OrderBy(s => s.Pos).ThenBy(s => s.Start)
            .ToList();

        var merged = new List<(double Pos, double Start, double End)>();
        foreach (var s in norm)
        {
            int idx = merged.FindLastIndex(m => Math.Abs(m.Pos - s.Pos) <= 1.5 && s.Start <= m.End + Tol);
            if (idx >= 0)
            {
                var m = merged[idx];
                merged[idx] = (m.Pos, Math.Min(m.Start, s.Start), Math.Max(m.End, s.End));
            }
            else merged.Add(s);
        }

        return merged.Select(m => horizontal
            ? new LineSegment(m.Start, m.Pos, m.End, m.Pos)
            : new LineSegment(m.Pos, m.Start, m.Pos, m.End)).ToList();
    }

    private static List<double> Cluster(IEnumerable<double> values)
    {
        var result = new List<List<double>>();
        foreach (var x in values.OrderBy(x => x))
        {
            if (result.Count > 0 && x - result[^1][^1] <= Tol) result[^1].Add(x);
            else result.Add([x]);
        }
        return result.Select(g => g.Average()).ToList();
    }
}
