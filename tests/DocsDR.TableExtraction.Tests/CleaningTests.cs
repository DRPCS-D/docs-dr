using ClosedXML.Excel;
using DocsDR.Core;
using DocsDR.Excel;

namespace DocsDR.TableExtraction.Tests;

public class CleaningTests
{
    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("$ 1.500.000", 1500000)]
    [InlineData("(1.000,00)", -1000)]
    [InlineData("250,00-", -250)]
    [InlineData("-3.5", -3.5)]
    [InlineData("12,5%", 0.125)]
    [InlineData("USD 99.90", 99.90)]
    public void Parses_numbers(string raw, double expected)
    {
        Assert.True(NumberParser.TryParse(raw, DecimalSeparator.Auto, out var v, out _));
        Assert.Equal((decimal)expected, v);
    }

    [Fact]
    public void Ambiguous_number_uses_column_evidence()
    {
        // "1.234" solo es ambiguo; la columna tiene "5,50" → coma decimal → 1.234 = mil doscientos...
        var dec = NumberParser.DetectColumn(["1.234", "5,50", "7,25"]);
        Assert.Equal(DecimalSeparator.Comma, dec);
        Assert.True(NumberParser.TryParse("1.234", dec, out var v, out _));
        Assert.Equal(1234m, v);

        dec = NumberParser.DetectColumn(["1.234", "5.50"]);
        Assert.Equal(DecimalSeparator.Dot, dec);
        Assert.True(NumberParser.TryParse("1.234", dec, out v, out _));
        Assert.Equal(1.234m, v);
    }

    [Theory]
    [InlineData("05/03/2024", 2024, 3, 5)]
    [InlineData("15-ene-2024", 2024, 1, 15)]
    [InlineData("5 de marzo de 2024", 2024, 3, 5)]
    [InlineData("2024-12-31", 2024, 12, 31)]
    [InlineData("01.02.24", 2024, 2, 1)]
    [InlineData("3 sept. 2025", 2025, 9, 3)]
    public void Parses_dates(string raw, int y, int m, int d)
    {
        Assert.True(DateParser.TryParse(raw, out var v));
        Assert.Equal(new DateTime(y, m, d), v.Date);
    }

    [Theory]
    [InlineData("Pago")]
    [InlineData("12345")]
    [InlineData("1/2")]
    public void Rejects_non_dates(string raw) => Assert.False(DateParser.TryParse(raw, out _));

    [Fact]
    public void Keeps_codes_as_text_and_merges_wrapped_rows()
    {
        var raw = new RawTable
        {
            Headers = ["Cuenta", "Detalle", "Monto"],
            Rows =
            [
                ["00123", "Compra de insumos", "1.000,00"],
                ["", "de oficina", ""],
                ["00456", "Arriendo", "2.500,50"],
                ["Cuenta", "Detalle", "Monto"],
            ],
        };

        var t = DataCleaner.Clean(raw);

        Assert.Equal(ColumnType.Text, t.Types[0]);
        Assert.Equal(ColumnType.Number, t.Types[2]);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("Compra de insumos de oficina", t.Rows[0][1].Value);
        Assert.Equal("00123", t.Rows[0][0].Value);
        Assert.Equal(2500.50m, t.Rows[1][2].Value);
    }

    [Fact]
    public void Exports_typed_cells_to_excel()
    {
        var table = DataCleaner.Clean(new RawTable
        {
            Name = "Tabla 1 (pág. 1)",
            Headers = ["Fecha", "Valor", "Cuenta"],
            Rows = [["05/03/2024", "1.234,56", "00123"], ["06/03/2024", "10,00", "00456"]],
            SourcePages = [1],
        });
        var path = Path.Combine(Path.GetTempPath(), $"docsdr-{Guid.NewGuid():N}.xlsx");

        ExcelExporter.Export([table], path);

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet(1);
        Assert.Equal("Fecha", ws.Cell(1, 1).GetString());
        Assert.Equal(XLDataType.DateTime, ws.Cell(2, 1).DataType);
        Assert.Equal(XLDataType.Number, ws.Cell(2, 2).DataType);
        Assert.Equal(1234.56, ws.Cell(2, 2).GetDouble(), 3);
        Assert.Equal(XLDataType.Text, ws.Cell(2, 3).DataType);
        Assert.Equal("00123", ws.Cell(2, 3).GetString());
        Assert.NotNull(wb.Worksheets.FirstOrDefault(s => s.Name == "Origen"));
    }
}
