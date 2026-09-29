using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>
/// Detecta líneas horizontales y verticales largas en una imagen escaneada (bordes de tabla)
/// buscando corridas continuas de píxeles oscuros. Devuelve segmentos en coordenadas de página.
/// </summary>
public static class ImageLineDetector
{
    public static List<LineSegment> Detect(GrayImage img, byte darkThreshold = 140, double minLengthPt = 25, int maxGapPx = 2)
    {
        int minLen = Math.Max(10, (int)(minLengthPt * img.Scale));
        var horizontal = new List<(int Pos, int Start, int End)>();
        var vertical = new List<(int Pos, int Start, int End)>();

        for (int y = 0; y < img.Height; y++)
            FindRuns(i => img.Pixels[y * img.Width + i] < darkThreshold, img.Width, minLen, maxGapPx,
                     (s, e) => horizontal.Add((y, s, e)));

        for (int x = 0; x < img.Width; x++)
            FindRuns(i => img.Pixels[i * img.Width + x] < darkThreshold, img.Height, minLen, maxGapPx,
                     (s, e) => vertical.Add((x, s, e)));

        var result = new List<LineSegment>();
        double k = 1.0 / img.Scale;
        foreach (var (pos, s, e) in MergeThick(horizontal))
            result.Add(new LineSegment(s * k, pos * k, e * k, pos * k));
        foreach (var (pos, s, e) in MergeThick(vertical))
            result.Add(new LineSegment(pos * k, s * k, pos * k, e * k));
        return result;
    }

    private static void FindRuns(Func<int, bool> isDark, int length, int minLen, int maxGap, Action<int, int> emit)
    {
        int start = -1, lastDark = -1;
        for (int i = 0; i < length; i++)
        {
            if (!isDark(i)) continue;
            if (start < 0) start = i;
            else if (i - lastDark > maxGap + 1)
            {
                if (lastDark - start + 1 >= minLen) emit(start, lastDark);
                start = i;
            }
            lastDark = i;
        }
        if (start >= 0 && lastDark - start + 1 >= minLen) emit(start, lastDark);
    }

    /// <summary>Une corridas en filas/columnas adyacentes (líneas de varios píxeles de grosor).</summary>
    private static IEnumerable<(double Pos, int Start, int End)> MergeThick(List<(int Pos, int Start, int End)> runs)
    {
        var groups = new List<List<(int Pos, int Start, int End)>>();
        foreach (var r in runs.OrderBy(r => r.Pos))
        {
            var g = groups.FindLast(g => r.Pos - g[^1].Pos <= 2 && g.Any(o => o.Start <= r.End && r.Start <= o.End));
            if (g is null) groups.Add([r]);
            else g.Add(r);
        }
        foreach (var g in groups)
            yield return (g.Average(r => r.Pos), g.Min(r => r.Start), g.Max(r => r.End));
    }
}
