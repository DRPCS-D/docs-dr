using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocsDR.TableExtraction;

public enum DecimalSeparator { Auto, Comma, Dot }

[Flags]
public enum NumberMarks { None = 0, Currency = 1, Percent = 2 }

public static partial class NumberParser
{
    [GeneratedRegex(@"^(US\$|USD|COP|EUR|MXN|PEN|CLP|ARS|S/\.?|\$|€|£)", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyPrefix();

    [GeneratedRegex(@"(US\$|USD|COP|EUR|MXN|PEN|CLP|ARS|\$|€|£)$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencySuffix();

    [GeneratedRegex(@"^[0-9][0-9.,]*$")]
    private static partial Regex Digits();

    /// <summary>Quita símbolos y signos; devuelve la parte numérica (solo dígitos, '.' y ',').</summary>
    public static bool TryNormalize(string raw, out string digits, out bool negative, out NumberMarks marks)
    {
        digits = "";
        negative = false;
        marks = NumberMarks.None;
        var s = new StringBuilder();
        foreach (var ch in raw.Trim())
            if (!char.IsWhiteSpace(ch) && ch != ' ' && ch != ' ' && ch != '\'') s.Append(ch == '−' ? '-' : ch);
        var t = s.ToString();
        if (t.Length == 0) return false;

        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.StartsWith('-')) { negative = true; t = t[1..]; }
        else if (t.StartsWith('+')) t = t[1..];
        if (t.EndsWith('-')) { negative = true; t = t[..^1]; }

        for (int pass = 0; pass < 2; pass++)
        {
            var m = CurrencyPrefix().Match(t);
            if (m.Success) { marks |= NumberMarks.Currency; t = t[m.Length..]; }
            m = CurrencySuffix().Match(t);
            if (m.Success) { marks |= NumberMarks.Currency; t = t[..^m.Length]; }
            if (t.EndsWith('%')) { marks |= NumberMarks.Percent; t = t[..^1]; }
            if (t.StartsWith('-')) { negative = true; t = t[1..]; }
        }

        if (!Digits().IsMatch(t)) return false;
        digits = t;
        return true;
    }

    /// <summary>
    /// Evidencia del separador decimal de un número: Comma, Dot o Auto (ambiguo, p.ej. "1.234").
    /// </summary>
    public static DecimalSeparator Evidence(string digits)
    {
        int dots = digits.Count(c => c == '.'), commas = digits.Count(c => c == ',');
        if (dots > 0 && commas > 0) return digits.LastIndexOf(',') > digits.LastIndexOf('.') ? DecimalSeparator.Comma : DecimalSeparator.Dot;
        if (dots > 1) return DecimalSeparator.Comma;
        if (commas > 1) return DecimalSeparator.Dot;
        if (dots == 1) return DigitsAfter(digits, '.') == 3 ? DecimalSeparator.Auto : DecimalSeparator.Dot;
        if (commas == 1) return DigitsAfter(digits, ',') == 3 ? DecimalSeparator.Auto : DecimalSeparator.Comma;
        return DecimalSeparator.Auto;
    }

    private static int DigitsAfter(string s, char sep) => s.Length - s.LastIndexOf(sep) - 1;

    /// <summary>Vota el separador decimal de una columna completa.</summary>
    public static DecimalSeparator DetectColumn(IEnumerable<string> values)
    {
        int comma = 0, dot = 0;
        foreach (var v in values)
        {
            if (!TryNormalize(v, out var d, out _, out _)) continue;
            switch (Evidence(d))
            {
                case DecimalSeparator.Comma: comma++; break;
                case DecimalSeparator.Dot: dot++; break;
            }
        }
        if (comma == 0 && dot == 0) return DecimalSeparator.Auto;
        return comma >= dot ? DecimalSeparator.Comma : DecimalSeparator.Dot;
    }

    public static bool TryParse(string raw, DecimalSeparator dec, out decimal value, out NumberMarks marks)
    {
        value = 0;
        if (!TryNormalize(raw, out var d, out bool negative, out marks)) return false;

        if (dec == DecimalSeparator.Auto) dec = Evidence(d);
        string normalized = dec switch
        {
            DecimalSeparator.Comma => d.Replace(".", "").Replace(',', '.'),
            DecimalSeparator.Dot => d.Replace(",", ""),
            _ => d.Replace(".", "").Replace(",", ""), // ambiguo sin evidencia: separador de miles
        };
        if (normalized.Count(c => c == '.') > 1) return false;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (negative) value = -value;
        if (marks.HasFlag(NumberMarks.Percent)) value /= 100m;
        return true;
    }
}

public static partial class DateParser
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ene"] = 1, ["enero"] = 1, ["jan"] = 1, ["january"] = 1,
        ["feb"] = 2, ["febrero"] = 2, ["february"] = 2,
        ["mar"] = 3, ["marzo"] = 3, ["march"] = 3,
        ["abr"] = 4, ["abril"] = 4, ["apr"] = 4, ["april"] = 4,
        ["may"] = 5, ["mayo"] = 5,
        ["jun"] = 6, ["junio"] = 6, ["june"] = 6,
        ["jul"] = 7, ["julio"] = 7, ["july"] = 7,
        ["ago"] = 8, ["agosto"] = 8, ["aug"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["set"] = 9, ["septiembre"] = 9, ["setiembre"] = 9, ["september"] = 9,
        ["oct"] = 10, ["octubre"] = 10, ["october"] = 10,
        ["nov"] = 11, ["noviembre"] = 11, ["november"] = 11,
        ["dic"] = 12, ["diciembre"] = 12, ["dec"] = 12, ["december"] = 12,
    };

    private static readonly string[] Formats =
    [
        "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy", "yyyy-M-d", "yyyy/M/d", "d M yyyy",
        "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "yyyy-M-d H:mm", "yyyy-M-d H:mm:ss", "yyyy-M-dTH:mm:ss",
    ];

    [GeneratedRegex(@"\p{L}+\.?")]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static bool TryParse(string raw, out DateTime value)
    {
        value = default;
        var s = raw.Trim();
        if (s.Length < 6 || s.Length > 30) return false;

        // Sustituye nombres de mes por su número y elimina "de" ("5 de marzo de 2024").
        bool invalidWord = false;
        s = Word().Replace(s, m =>
        {
            var w = m.Value.TrimEnd('.');
            if (w.Equals("de", StringComparison.OrdinalIgnoreCase) || w.Equals("del", StringComparison.OrdinalIgnoreCase)) return " ";
            if (w == "T") return "T";
            if (Months.TryGetValue(w, out int month)) return month.ToString(CultureInfo.InvariantCulture);
            invalidWord = true;
            return w;
        });
        if (invalidWord) return false;
        s = Spaces().Replace(s.Replace(",", " "), " ").Trim();
        s = s.Replace(" - ", "-").Replace(" / ", "/");

        return DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value)
               && value.Year is > 1900 and < 2200;
    }
}
