using DocsDR.Core;

namespace DocsDR.TableExtraction;

/// <summary>Renglón de texto: palabras con la misma línea base aproximada, ordenadas por X.</summary>
public sealed class TextLine
{
    public List<TextWord> Words { get; } = [];
    public PdfRect Box { get; private set; }

    public double CenterY => Box.CenterY;

    public void Add(TextWord w)
    {
        Box = Words.Count == 0 ? w.Box : Box.Union(w.Box);
        Words.Add(w);
    }

    /// <summary>Divide el renglón en bloques separados por huecos mayores a <paramref name="minGap"/>.</summary>
    public List<List<TextWord>> Segments(double minGap)
    {
        var result = new List<List<TextWord>>();
        List<TextWord>? current = null;
        double lastX1 = double.NegativeInfinity;
        foreach (var w in Words)
        {
            if (current is null || w.Box.X0 - lastX1 > minGap)
            {
                current = [];
                result.Add(current);
            }
            current.Add(w);
            lastX1 = Math.Max(lastX1, w.Box.X1);
        }
        return result;
    }
}

public static class TextLines
{
    /// <summary>Agrupa palabras en renglones según su centro vertical.</summary>
    public static List<TextLine> Group(IEnumerable<TextWord> words)
    {
        var lines = new List<TextLine>();
        foreach (var w in words.OrderBy(w => w.Box.CenterY).ThenBy(w => w.Box.X0))
        {
            var line = lines.Count > 0 ? lines[^1] : null;
            double tol = Math.Max(1.5, Math.Min(w.Box.Height, line?.Box.Height ?? w.Box.Height) * 0.5);
            if (line is null || Math.Abs(w.Box.CenterY - line.CenterY) > tol)
            {
                line = new TextLine();
                lines.Add(line);
            }
            line.Add(w);
        }
        foreach (var l in lines) l.Words.Sort((a, b) => a.Box.X0.CompareTo(b.Box.X0));
        return lines;
    }

    public static double MedianHeight(IEnumerable<TextWord> words)
    {
        var h = words.Select(w => w.Box.Height).Where(v => v > 0).OrderBy(v => v).ToList();
        return h.Count == 0 ? 10 : h[h.Count / 2];
    }
}
