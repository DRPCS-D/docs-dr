namespace DocsDR.Core;

public enum DetectionMethod { Lattice, Stream, Manual }

/// <summary>
/// Definición de una tabla sobre una página: región + separadores de columna (y opcionalmente de fila).
/// Es lo que el usuario ajusta en el editor manual.
/// </summary>
public sealed class TableDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int PageIndex { get; set; }
    public PdfRect Region { get; set; }

    /// <summary>Posiciones X de los separadores internos de columna (ordenadas).</summary>
    public List<double> ColumnSeparators { get; set; } = [];

    /// <summary>Posiciones Y de separadores internos de fila. Vacía = filas automáticas (una por renglón de texto).</summary>
    public List<double> RowSeparators { get; set; } = [];

    public int HeaderRows { get; set; } = 1;
    public DetectionMethod Method { get; set; }
    public double Confidence { get; set; } = 1.0;

    /// <summary>null = decidir automáticamente; true/false = forzar unión con la tabla de la página anterior.</summary>
    public bool? ContinuesPrevious { get; set; }

    public int ColumnCount => ColumnSeparators.Count + 1;

    public TableDefinition Clone() => new()
    {
        PageIndex = PageIndex,
        Region = Region,
        ColumnSeparators = [.. ColumnSeparators],
        RowSeparators = [.. RowSeparators],
        HeaderRows = HeaderRows,
        Method = Method,
        Confidence = Confidence,
        ContinuesPrevious = ContinuesPrevious,
    };
}

/// <summary>Celdas de texto extraídas de una tabla de una página.</summary>
public sealed record TableGrid(TableDefinition Definition, IReadOnlyList<string[]> Rows)
{
    public int ColumnCount => Definition.ColumnCount;
}

/// <summary>Tabla lógica (posiblemente unida a través de varias páginas) aún como texto.</summary>
public sealed class RawTable
{
    public string Name { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];
    public List<int> SourcePages { get; set; } = [];
    public int ColumnCount => Headers.Count > 0 ? Headers.Count : Rows.FirstOrDefault()?.Length ?? 0;
}

public enum ColumnType { Text, Number, Currency, Percent, Date }

/// <summary>Valor de celda ya tipado: string, decimal o DateTime (o null si está vacía).</summary>
public readonly record struct CellValue(object? Value, string Raw)
{
    public static CellValue Empty => new(null, "");
    public bool IsEmpty => Value is null || Value is string s && s.Length == 0;
    public override string ToString() => Raw;
}

/// <summary>Tabla final lista para exportar.</summary>
public sealed class CleanTable
{
    public string Name { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public List<ColumnType> Types { get; set; } = [];
    public List<CellValue[]> Rows { get; set; } = [];
    public List<int> SourcePages { get; set; } = [];
}
