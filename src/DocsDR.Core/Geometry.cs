namespace DocsDR.Core;

/// <summary>Rectángulo en coordenadas de página PDF (puntos, origen arriba-izquierda, Y hacia abajo).</summary>
public readonly record struct PdfRect(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;

    public bool ContainsCenterOf(PdfRect r) => Contains(r.CenterX, r.CenterY);

    public bool Intersects(PdfRect r) => r.X0 < X1 && r.X1 > X0 && r.Y0 < Y1 && r.Y1 > Y0;

    public PdfRect Union(PdfRect r) =>
        new(Math.Min(X0, r.X0), Math.Min(Y0, r.Y0), Math.Max(X1, r.X1), Math.Max(Y1, r.Y1));

    public PdfRect Inflate(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);

    public static PdfRect FromPoints(double ax, double ay, double bx, double by) =>
        new(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
}

/// <summary>Segmento de línea (borde de tabla) en coordenadas de página.</summary>
public readonly record struct LineSegment(double X0, double Y0, double X1, double Y1)
{
    public const double AxisTolerance = 1.0;

    public bool IsHorizontal => Math.Abs(Y1 - Y0) <= AxisTolerance && Math.Abs(X1 - X0) > AxisTolerance;
    public bool IsVertical => Math.Abs(X1 - X0) <= AxisTolerance && Math.Abs(Y1 - Y0) > AxisTolerance;
    public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
}

/// <summary>Palabra con su caja delimitadora.</summary>
public sealed record TextWord(string Text, PdfRect Box);
