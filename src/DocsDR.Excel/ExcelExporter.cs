using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using DocsDR.Core;

namespace DocsDR.Excel;

public sealed class ExcelExportOptions
{
    /// <summary>true = todas las tablas en una hoja; false = una hoja por tabla.</summary>
    public bool Consolidated { get; set; }

    /// <summary>Agrega una hoja "Origen" con tabla, páginas y filas.</summary>
    public bool IncludeSourceSheet { get; set; } = true;
}

public static class ExcelExporter
{
    private const double MaxColumnWidth = 60;

    public static void Export(IReadOnlyList<CleanTable> tables, string path, ExcelExportOptions? options = null)
    {
        options ??= new ExcelExportOptions();
        using var wb = new XLWorkbook();

        if (options.Consolidated) WriteConsolidated(wb, tables);
        else
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                var ws = wb.Worksheets.Add(UniqueSheetName(t.Name, used));
                int last = WriteTable(ws, t, 1);
                var range = ws.Range(1, 1, Math.Max(last, 1), Math.Max(t.Headers.Count, 1));
                if (t.Rows.Count > 0) range.SetAutoFilter();
                ws.SheetView.FreezeRows(1);
                FitColumns(ws);
            }
        }

        if (tables.Count == 0) wb.Worksheets.Add("Datos");

        if (options.IncludeSourceSheet && tables.Count > 0)
        {
            var src = wb.Worksheets.Add("Origen");
            src.Cell(1, 1).Value = "Tabla";
            src.Cell(1, 2).Value = "Páginas";
            src.Cell(1, 3).Value = "Filas";
            StyleHeader(src.Range(1, 1, 1, 3));
            for (int i = 0; i < tables.Count; i++)
            {
                src.Cell(i + 2, 1).Value = tables[i].Name;
                src.Cell(i + 2, 2).Value = string.Join(", ", tables[i].SourcePages);
                src.Cell(i + 2, 3).Value = tables[i].Rows.Count;
            }
            FitColumns(src);
        }

        wb.SaveAs(path);
    }

    /// <summary>Si todas las tablas tienen los mismos encabezados se unen en una sola; si no, se apilan con título.</summary>
    private static void WriteConsolidated(XLWorkbook wb, IReadOnlyList<CleanTable> tables)
    {
        var ws = wb.Worksheets.Add("Datos");
        if (tables.Count == 0) return;

        bool sameShape = tables.All(t => t.Headers.SequenceEqual(tables[0].Headers, StringComparer.OrdinalIgnoreCase));
        if (sameShape)
        {
            var merged = new CleanTable
            {
                Name = "Datos",
                Headers = tables[0].Headers,
                Types = tables[0].Types,
                Rows = tables.SelectMany(t => t.Rows).ToList(),
                SourcePages = tables.SelectMany(t => t.SourcePages).Distinct().ToList(),
            };
            int last = WriteTable(ws, merged, 1);
            if (merged.Rows.Count > 0) ws.Range(1, 1, last, merged.Headers.Count).SetAutoFilter();
            ws.SheetView.FreezeRows(1);
        }
        else
        {
            int row = 1;
            foreach (var t in tables)
            {
                ws.Cell(row, 1).Value = t.Name;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                row = WriteTable(ws, t, row + 1) + 2;
            }
        }
        FitColumns(ws);
    }

    /// <summary>Escribe encabezado + filas desde <paramref name="startRow"/>; devuelve la última fila usada.</summary>
    private static int WriteTable(IXLWorksheet ws, CleanTable t, int startRow)
    {
        for (int c = 0; c < t.Headers.Count; c++) ws.Cell(startRow, c + 1).Value = t.Headers[c];
        StyleHeader(ws.Range(startRow, 1, startRow, Math.Max(t.Headers.Count, 1)));

        int r = startRow;
        foreach (var row in t.Rows)
        {
            r++;
            for (int c = 0; c < row.Length; c++)
            {
                var cell = ws.Cell(r, c + 1);
                var type = c < t.Types.Count ? t.Types[c] : ColumnType.Text;
                switch (row[c].Value)
                {
                    case null: break;
                    case decimal d:
                        cell.Value = d;
                        cell.Style.NumberFormat.Format = NumberFormat(type, t, c);
                        break;
                    case DateTime dt:
                        cell.Value = dt;
                        cell.Style.DateFormat.Format = dt.TimeOfDay == TimeSpan.Zero ? "dd/mm/yyyy" : "dd/mm/yyyy hh:mm";
                        break;
                    default:
                        cell.Value = row[c].Raw;
                        // Evita que Excel reinterprete textos (códigos, cuentas) como números.
                        cell.Style.NumberFormat.Format = "@";
                        break;
                }
            }
        }
        return r;
    }

    private static string NumberFormat(ColumnType type, CleanTable t, int col)
    {
        if (type == ColumnType.Percent) return "0.00%";
        bool allIntegers = t.Rows.All(r => r[col].Value is not decimal d || d == decimal.Truncate(d));
        return allIntegers ? "#,##0" : "#,##0.00";
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    private static void FitColumns(IXLWorksheet ws)
    {
        foreach (var col in ws.ColumnsUsed())
        {
            col.AdjustToContents();
            if (col.Width > MaxColumnWidth) col.Width = MaxColumnWidth;
        }
    }

    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim('\'');
        if (clean.Length == 0) clean = "Tabla";
        if (clean.Length > 31) clean = clean[..31];
        var candidate = clean;
        for (int i = 2; !used.Add(candidate); i++)
        {
            var suffix = $" ({i})";
            candidate = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix;
        }
        return candidate;
    }
}

public static class CsvExporter
{
    /// <summary>
    /// Exporta cada tabla a un CSV con separador ';' y números en formato de la cultura actual,
    /// que es lo que espera Excel en configuración regional en español.
    /// Con una sola tabla usa <paramref name="path"/>; con varias agrega el sufijo _1, _2...
    /// </summary>
    public static List<string> Export(IReadOnlyList<CleanTable> tables, string path)
    {
        var files = new List<string>();
        var culture = CultureInfo.CurrentCulture;
        for (int i = 0; i < tables.Count; i++)
        {
            var file = tables.Count == 1 ? path
                : Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}_{i + 1}{Path.GetExtension(path)}");
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(';', tables[i].Headers.Select(Escape)));
            foreach (var row in tables[i].Rows)
                sb.AppendLine(string.Join(';', row.Select(v => Escape(v.Value switch
                {
                    decimal d => d.ToString(culture),
                    DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", culture),
                    _ => v.Raw,
                }))));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            files.Add(file);
        }
        return files;
    }

    private static string Escape(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
