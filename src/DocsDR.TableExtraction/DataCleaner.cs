using DocsDR.Core;

namespace DocsDR.TableExtraction;

public sealed class CleanOptions
{
    public bool TrimWhitespace { get; set; } = true;
    public bool RemoveRepeatedHeaders { get; set; } = true;
    public bool MergeWrappedRows { get; set; } = true;
    public bool DetectTypes { get; set; } = true;
    public DecimalSeparator DecimalSeparator { get; set; } = DecimalSeparator.Auto;

    /// <summary>Tipo forzado por columna (índice → tipo). Sin entrada = automático.</summary>
    public Dictionary<int, ColumnType> ColumnTypeOverrides { get; set; } = [];
}

/// <summary>Convierte una <see cref="RawTable"/> de texto en una <see cref="CleanTable"/> con valores tipados.</summary>
public static class DataCleaner
{
    private const double TypeThreshold = 0.8;

    public static CleanTable Clean(RawTable raw, CleanOptions? options = null)
    {
        options ??= new CleanOptions();
        int cols = raw.ColumnCount;
        var headers = raw.Headers.Select(h => options.TrimWhitespace ? Collapse(h) : h).ToList();
        while (headers.Count < cols) headers.Add($"Columna {headers.Count + 1}");

        var rows = raw.Rows
            .Select(r => Enumerable.Range(0, cols).Select(c => c < r.Length ? (options.TrimWhitespace ? Collapse(r[c]) : r[c]) : "").ToArray())
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();

        if (options.RemoveRepeatedHeaders)
        {
            var headerKey = MultiPageMerger.Normalize(string.Join(" ", headers));
            rows.RemoveAll(r => MultiPageMerger.Normalize(string.Join(" ", r.Where(c => c.Length > 0))) == headerKey);
        }

        var decimals = Enumerable.Range(0, cols)
            .Select(c => options.DecimalSeparator != DecimalSeparator.Auto
                ? options.DecimalSeparator
                : NumberParser.DetectColumn(rows.Select(r => r[c])))
            .ToArray();

        var types = Enumerable.Range(0, cols)
            .Select(c => options.ColumnTypeOverrides.TryGetValue(c, out var t) ? t
                : options.DetectTypes ? InferType(rows.Select(r => r[c]), decimals[c]) : ColumnType.Text)
            .ToList();

        if (options.MergeWrappedRows) rows = MergeWrapped(rows, types);

        return new CleanTable
        {
            Name = raw.Name,
            Headers = headers,
            Types = types,
            SourcePages = [.. raw.SourcePages],
            Rows = rows.Select(r => r.Select((v, c) => Parse(v, types[c], decimals[c])).ToArray()).ToList(),
        };
    }

    /// <summary>Convierte un texto al valor del tipo indicado (si no se puede, queda como texto).</summary>
    public static CellValue Parse(string raw, ColumnType type, DecimalSeparator dec)
    {
        if (raw.Length == 0) return CellValue.Empty;
        switch (type)
        {
            case ColumnType.Number or ColumnType.Currency or ColumnType.Percent:
                if (NumberParser.TryParse(raw, dec, out var n, out _)) return new CellValue(n, raw);
                break;
            case ColumnType.Date:
                if (DateParser.TryParse(raw, out var d)) return new CellValue(d, raw);
                break;
        }
        return new CellValue(raw, raw);
    }

    public static ColumnType InferType(IEnumerable<string> values, DecimalSeparator dec)
    {
        var nonEmpty = values.Where(v => v.Length > 0).ToList();
        if (nonEmpty.Count == 0) return ColumnType.Text;

        int dates = nonEmpty.Count(v => DateParser.TryParse(v, out _));
        if (dates >= nonEmpty.Count * TypeThreshold) return ColumnType.Date;

        int numbers = 0, currency = 0, percent = 0, codeLike = 0;
        foreach (var v in nonEmpty)
        {
            if (!NumberParser.TryParse(v, dec, out _, out var marks)) continue;
            numbers++;
            if (marks.HasFlag(NumberMarks.Currency)) currency++;
            if (marks.HasFlag(NumberMarks.Percent)) percent++;
            if (LooksLikeCode(v)) codeLike++;
        }
        if (numbers < nonEmpty.Count * TypeThreshold) return ColumnType.Text;
        // Identificadores (ceros a la izquierda, cuentas largas) se conservan como texto.
        if (codeLike >= numbers * 0.5) return ColumnType.Text;
        if (percent >= numbers * 0.5) return ColumnType.Percent;
        if (currency >= numbers * 0.5) return ColumnType.Currency;
        return ColumnType.Number;
    }

    private static bool LooksLikeCode(string v)
    {
        var t = v.Trim();
        if (t.Length > 1 && t[0] == '0' && t.All(char.IsDigit)) return true;
        return t.Count(char.IsDigit) > 15;
    }

    /// <summary>
    /// Une filas que son continuación de la anterior: primera columna vacía y solo texto en columnas de texto.
    /// </summary>
    private static List<string[]> MergeWrapped(List<string[]> rows, List<ColumnType> types)
    {
        var result = new List<string[]>();
        foreach (var row in rows)
        {
            bool continuation = result.Count > 0
                && row[0].Length == 0
                && row.Select((v, c) => v.Length == 0 || types[c] == ColumnType.Text).All(ok => ok)
                && row.Any(v => v.Length > 0)
                && types.Any(t => t != ColumnType.Text); // solo en tablas con alguna columna tipada (evita falsos positivos)

            if (continuation)
            {
                var prev = result[^1];
                for (int c = 0; c < row.Length; c++)
                    if (row[c].Length > 0) prev[c] = prev[c].Length > 0 ? prev[c] + " " + row[c] : row[c];
            }
            else result.Add(row);
        }
        return result;
    }

    private static string Collapse(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
