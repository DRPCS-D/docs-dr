using System.Windows.Media;
using DocsDR.Core;

namespace DocsDR.App.ViewModels;

public enum AnnotTool
{
    Select, Highlight, Underline, StrikeOut, Note, FreeText, Ink, Rectangle, Ellipse, Line, Arrow, Stamp, Image,
    // Edición del contenido existente de la página
    EditText, EditObjects, AddText, AddPageImage,
}

public sealed record ColorOption(string Name, PdfColor Value)
{
    public Brush Brush { get; } = Freeze(new SolidColorBrush(Color.FromRgb(Value.R, Value.G, Value.B)));

    public static IReadOnlyList<ColorOption> All { get; } =
    [
        new("Amarillo", PdfColor.Yellow),
        new("Verde", PdfColor.Green),
        new("Azul", PdfColor.Blue),
        new("Rosa", PdfColor.Pink),
        new("Naranja", PdfColor.Orange),
        new("Rojo", PdfColor.Red),
        new("Negro", PdfColor.Black),
    ];

    public static ColorOption Of(PdfColor c) => All.FirstOrDefault(o => o.Value == c) ?? All[0];

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

/// <summary>Sello predefinido: texto enmarcado que se convierte en imagen al colocarlo.</summary>
public sealed record StampOption(string Name, string Text, PdfColor Color)
{
    public static IReadOnlyList<StampOption> All { get; } =
    [
        new("Aprobado", "APROBADO", new PdfColor(30, 140, 60)),
        new("Rechazado", "RECHAZADO", new PdfColor(200, 30, 30)),
        new("Borrador", "BORRADOR", new PdfColor(40, 100, 200)),
        new("Confidencial", "CONFIDENCIAL", new PdfColor(200, 30, 30)),
        new("Copia", "COPIA", new PdfColor(40, 100, 200)),
        new("Revisado", "REVISADO", new PdfColor(200, 110, 0)),
        new("Urgente", "URGENTE", new PdfColor(200, 30, 30)),
        new("Fecha de hoy", "", new PdfColor(40, 100, 200)), // el texto se calcula al colocarlo
    ];

    public string ResolvedText => Text.Length > 0 ? Text : DateTime.Now.ToString("dd/MM/yyyy");
}

/// <summary>Fila del panel de comentarios.</summary>
public sealed record AnnotationItem(AnnotationInfo Info)
{
    public string PageLabel => $"Pág. {Info.PageIndex + 1}";
    public string Author => Info.Author;
    public string Preview => Info.Content;
    public bool HasPreview => Info.Content.Length > 0;
    public string Title => $"{Icon} {TypeName} · {PageLabel}";

    public string TypeName => Info.Kind switch
    {
        AnnotationKind.Highlight => "Resaltado",
        AnnotationKind.Underline => "Subrayado",
        AnnotationKind.StrikeOut => "Tachado",
        AnnotationKind.Note => "Nota",
        AnnotationKind.FreeText => "Texto",
        AnnotationKind.Ink => "Dibujo",
        AnnotationKind.Rectangle => "Rectángulo",
        AnnotationKind.Ellipse => "Elipse",
        AnnotationKind.Line => "Línea",
        AnnotationKind.Arrow => "Flecha",
        AnnotationKind.Stamp => "Sello / imagen",
        _ => "Anotación",
    };

    public string Icon => Info.Kind switch
    {
        AnnotationKind.Highlight => "🖍",
        AnnotationKind.Underline => "U",
        AnnotationKind.StrikeOut => "S",
        AnnotationKind.Note => "🗒",
        AnnotationKind.FreeText => "T",
        AnnotationKind.Ink => "✎",
        AnnotationKind.Rectangle => "▭",
        AnnotationKind.Ellipse => "◯",
        AnnotationKind.Line => "╱",
        AnnotationKind.Arrow => "➝",
        AnnotationKind.Stamp => "▣",
        _ => "•",
    };
}
