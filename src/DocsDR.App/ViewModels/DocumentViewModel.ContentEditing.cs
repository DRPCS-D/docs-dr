using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocsDR.App.Services;
using DocsDR.Core;
using DocsDR.TableExtraction;
using Microsoft.Win32;

namespace DocsDR.App.ViewModels;

/// <summary>Una edición de texto en curso: qué texto se está cambiando (o nuevo) y con qué formato empezó.</summary>
public sealed class TextEditSession(
    PageViewModel page, TextBlockInfo? block, PdfRect box, string text, bool isParagraph,
    TextFormat originalFormat, TextAlign originalAlign, double baseline = double.NaN,
    IReadOnlyList<TextRun>? runs = null)
{
    /// <summary>Tramos con formato propio del renglón editado (null si es de un solo formato, un párrafo o texto nuevo).</summary>
    public IReadOnlyList<TextRun>? Runs { get; } = runs;

    /// <summary>Y de la línea base del primer renglón original (NaN si es texto nuevo).</summary>
    public double Baseline { get; } = baseline;

    public PageViewModel Page { get; } = page;

    /// <summary>Bloque de origen; null si es texto nuevo.</summary>
    public TextBlockInfo? Block { get; } = block;

    public PdfRect Box { get; } = box;
    public string Text { get; } = text;
    public bool IsParagraph { get; } = isParagraph;
    public bool IsNew => Block is null;
    public TextFormat OriginalFormat { get; } = originalFormat;
    public TextAlign OriginalAlign { get; } = originalAlign;
}

/// <summary>Edición del contenido existente de la página: texto e imágenes.</summary>
public sealed partial class DocumentViewModel
{
    // ================= Cachés de lo que hay en cada página =================

    private readonly Dictionary<int, IReadOnlyList<TextBlockInfo>> _textCache = [];
    private readonly Dictionary<int, IReadOnlyList<PageImageInfo>> _imageCache = [];

    private void InvalidateContentCache(int page)
    {
        _textCache.Remove(page);
        _imageCache.Remove(page);
    }

    private void ClearContentCaches()
    {
        _textCache.Clear();
        _imageCache.Clear();
    }

    private IReadOnlyList<TextBlockInfo> BlocksOf(int page)
    {
        if (Editor is not { } ed) return [];
        if (!_textCache.TryGetValue(page, out var blocks)) _textCache[page] = blocks = ed.GetTextBlocks(page);
        return blocks;
    }

    private IReadOnlyList<PageImageInfo> ImagesOf(int page)
    {
        if (Editor is not { } ed) return [];
        if (!_imageCache.TryGetValue(page, out var images)) _imageCache[page] = images = ed.GetImages(page);
        return images;
    }

    /// <summary>Una línea de ayuda sobre lo que hace la herramienta activa.</summary>
    public string ToolHint => Tool switch
    {
        AnnotTool.Select => "Selecciona texto arrastrando; haz clic en una anotación para elegirla (doble clic edita su texto).",
        AnnotTool.Highlight or AnnotTool.Underline or AnnotTool.StrikeOut => "Arrastra sobre el texto para marcarlo.",
        AnnotTool.Note => "Haz clic donde quieras colocar la nota.",
        AnnotTool.FreeText => "Arrastra un área (o haz clic) para escribir un cuadro de texto.",
        AnnotTool.Ink => "Dibuja a mano alzada arrastrando.",
        AnnotTool.Rectangle or AnnotTool.Ellipse or AnnotTool.Line or AnnotTool.Arrow => "Arrastra sobre la página para dibujar la forma.",
        AnnotTool.Stamp or AnnotTool.Image => "Haz clic en la página para colocarlo.",
        AnnotTool.EditText => ParagraphMode
            ? "Haz clic en un párrafo para editarlo entero (se reajusta). Cambia fuente, tamaño o color antes de aplicar."
            : "Haz clic en un renglón para editarlo. Cambia fuente, tamaño o color antes de aplicar. Intro aplica, Esc cancela.",
        AnnotTool.AddText => "Haz clic donde quieras escribir un texto nuevo; Ctrl+Intro lo aplica.",
        AnnotTool.EditObjects => "Haz clic en una imagen: arrástrala para moverla, arrastra una esquina para cambiar su tamaño, Supr la elimina.",
        AnnotTool.AddPageImage => "Haz clic en la página donde colocar la imagen elegida.",
        _ => "",
    };

    // ================= Formato del texto que se edita o se agrega =================

    public static IReadOnlyList<string> TextFamilies => TextFormat.Families;
    public static IReadOnlyList<string> TextAlignments { get; } = ["Izquierda", "Centro", "Derecha"];

    public ObservableCollection<double> TextSizes { get; } = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];

    /// <summary>Colores para el texto (negro primero). Al editar se agrega el color "Original" si no está en la lista.</summary>
    public ObservableCollection<ColorOption> TextColors { get; } =
        new(ColorOption.All.OrderBy(c => c.Value == PdfColor.Black ? 0 : 1));

    [ObservableProperty] private string _textFamily = TextFormat.Sans;
    [ObservableProperty] private double _textSize = 12;
    [ObservableProperty] private bool _textBold;
    [ObservableProperty] private bool _textItalic;
    [ObservableProperty] private ColorOption? _textColor = ColorOption.All.First(c => c.Value == PdfColor.Black);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextAlignIndex))]
    private TextAlign _textAlignment = TextAlign.Left;

    /// <summary>true = al hacer clic se edita el párrafo completo (y se reajusta); false = solo el renglón.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHint))]
    private bool _paragraphMode;

    public int TextAlignIndex
    {
        get => (int)TextAlignment;
        set => TextAlignment = (TextAlign)Math.Clamp(value, 0, 2);
    }

    private TextFormat CurrentFormat =>
        new(TextFamily, TextSize, (TextColor ?? ColorOption.All[^1]).Value, TextBold, TextItalic);

    /// <summary>Carga en los controles el formato del texto que se va a editar, para que el usuario lo ajuste.</summary>
    private void ApplyFormat(TextFormat f)
    {
        if (!TextSizes.Contains(f.Size))
        {
            int at = 0;
            while (at < TextSizes.Count && TextSizes[at] < f.Size) at++;
            TextSizes.Insert(at, f.Size);
        }
        TextFamily = f.Family;
        TextSize = f.Size;
        TextBold = f.Bold;
        TextItalic = f.Italic;

        var original = TextColors.FirstOrDefault(c => c.Name == "Original");
        if (original is not null) TextColors.Remove(original);
        var color = TextColors.FirstOrDefault(c => c.Value == f.Color);
        if (color is null) TextColors.Insert(0, color = new ColorOption("Original", f.Color));
        TextColor = color;
    }

    // ================= Edición de texto =================

    private static double DistanceTo(PdfRect r, PdfPoint p)
    {
        // Considera ambos ejes: dos textos pueden compartir renglon dentro de un mismo bloque.
        double dx = Math.Max(Math.Max(r.X0 - p.X, p.X - r.X1), 0);
        double dy = Math.Max(Math.Max(r.Y0 - p.Y, p.Y - r.Y1), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Empieza a editar el texto que hay bajo el punto. Devuelve null si ahí no hay texto editable.</summary>
    public TextEditSession? BeginTextEdit(PageViewModel page, PdfPoint pt)
    {
        if (Editor is null) return null;
        SelectAnnotation(null);
        SelectImage(null);
        ClearSelection();

        var block = BlocksOf(page.Index)
            .Where(b => b.Box.Inflate(2).Contains(pt.X, pt.Y))
            .OrderBy(b => b.Box.Width * b.Box.Height)
            .FirstOrDefault();
        if (block is null)
        {
            SearchStatus = "No hay texto editable en ese punto (¿página escaneada?)";
            return null;
        }

        bool paragraph = ParagraphMode && block.Lines.Count > 1;
        var line = paragraph ? null : block.Lines.OrderBy(l => DistanceTo(l.Box, pt)).First();
        var box = paragraph ? block.Box : line!.Box;
        var text = paragraph ? block.ParagraphText : line!.Text;
        var format = paragraph ? block.Format : line!.Format;

        // Las cifras suelen ir alineadas a la derecha en tablas y totales.
        var align = NumberParser.TryParse(text, DecimalSeparator.Auto, out _, out _) ? TextAlign.Right : TextAlign.Left;
        ApplyFormat(format);
        TextAlignment = align;
        double baseline = paragraph ? block.Lines[0].Baseline : line!.Baseline;
        return new TextEditSession(page, block, box, text, paragraph, format, align, baseline, paragraph ? null : line!.Runs);
    }

    /// <summary>Empieza un texto nuevo en el punto indicado, con el formato actual de la barra.</summary>
    public TextEditSession BeginNewText(PageViewModel page, PdfPoint pt)
    {
        SelectAnnotation(null);
        SelectImage(null);
        ClearSelection();
        // La alineacion que dejo una edicion anterior (p. ej. la automatica de una cifra) no debe pasar al texto nuevo.
        TextAlignment = TextAlign.Left;
        var box = new PdfRect(pt.X, pt.Y, Math.Min(page.PageWidth - 10, pt.X + 240), pt.Y + TextSize * 1.8);
        return new TextEditSession(page, null, box, "", isParagraph: true, CurrentFormat, TextAlignment);
    }

    /// <summary>¿Hay algo que aplicar? (texto distinto o formato cambiado). Evita reescribir texto sin motivo.</summary>
    public bool TextEditChanged(TextEditSession s, string text) =>
        s.IsNew ? !string.IsNullOrWhiteSpace(text)
                : text != s.Text || CurrentFormat != s.OriginalFormat || TextAlignment != s.OriginalAlign;

    public void CommitTextEdit(TextEditSession s, string text)
    {
        if (Editor is null) return;
        var format = CurrentFormat;
        var align = TextAlignment;
        int page = s.Page.Index;
        double used = format.Size;

        bool ok;
        if (s.IsNew)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var box = NewTextBox(s, text, format.Size);
            ok = Edit(ed => used = ed.AddText(page, box, text, format, align), false, page);
        }
        else
        {
            // Se borra un poco menos que el renglón para no llevarse el texto de los renglones vecinos.
            var b = s.Box;
            double inset = Math.Min(1.5, b.Height * 0.15);
            var erase = new PdfRect(b.X0, b.Y0 + inset, b.X1, b.Y1 - inset);
            if (!s.IsParagraph && !double.IsNaN(s.Baseline))
            {
                // MuPDF borra cualquier letra que toque el rectángulo. En titulares grandes los renglones se rozan,
                // así que se borra solo una franja alrededor de la línea base (cada letra se quita entera).
                double size = s.Runs?.Max(r => r.Format.Size) ?? s.OriginalFormat.Size;
                erase = new PdfRect(b.X0, Math.Max(erase.Y0, s.Baseline - 0.72 * size), b.X1, Math.Min(erase.Y1, s.Baseline));
            }
            var place = PlaceBoxOf(s, format.Size, align);
            double factor = s.IsParagraph && s.Block!.Lines.Count > 1
                ? (s.Block.Lines[^1].Box.Y0 - s.Block.Lines[0].Box.Y0) / (s.Block.Lines.Count - 1) / format.Size
                : 0;
            // Si no se tocó el formato, un renglón con varios formatos los conserva en lo que no se cambió.
            var runs = s.Runs is { Count: > 1 } && format == s.OriginalFormat && !s.IsParagraph
                ? TextRunMapper.Remap(s.Runs, text) : null;
            ok = Edit(ed => used = ed.ReplaceText(page, erase, place, text, format, align, factor,
                baseline: double.IsNaN(s.Baseline) ? null : s.Baseline, runs: runs), false, page);
        }
        if (!ok) return;

        SearchStatus = used < format.Size - 0.01
            ? $"Texto ajustado a {used:0.#} pt para que quepa"
            : $"Texto actualizado ({format.Family} {used:0.#} pt)";
    }

    /// <summary>Caja donde se escribe el texto nuevo: deja espacio a un lado según la alineación.</summary>
    private static PdfRect PlaceBoxOf(TextEditSession s, double size, TextAlign align)
    {
        var b = s.Box;
        if (s.IsParagraph) return b; // se reajusta dentro del bloque
        double pageW = s.Page.PageWidth;
        var block = s.Block!.Box;
        // Un renglón suelto (etiqueta, celda) puede crecer; uno dentro de un párrafo se limita al bloque.
        double slack = s.Block.Lines.Count == 1 ? 150 : 0;
        double y0 = b.Y0 - 0.5;
        double y1 = y0 + Math.Max(b.Height + 2, size * 1.8);
        switch (align)
        {
            case TextAlign.Right:
                return new PdfRect(Math.Max(5, (slack > 0 ? b.X0 : block.X0) - slack), y0, b.X1, y1);
            case TextAlign.Center:
                double half = Math.Max(b.Width, block.Width) / 2 + slack / 2;
                double cx = (b.X0 + b.X1) / 2;
                return new PdfRect(Math.Max(5, cx - half), y0, Math.Min(pageW - 5, cx + half), y1);
            default:
                return new PdfRect(b.X0, y0, Math.Min(pageW - 5, Math.Max(block.X1, b.X1) + slack), y1);
        }
    }

    private static PdfRect NewTextBox(TextEditSession s, string text, double size)
    {
        // Altura estimada: un renglón por salto de línea y otro por cada ~45 caracteres.
        int lines = text.Split('\n').Sum(l => 1 + l.Length / 45);
        double height = size * 1.8 * lines + 4;
        var b = s.Box;
        double bottom = Math.Min(s.Page.PageHeight - 8, b.Y0 + height);
        return new PdfRect(b.X0, b.Y0, b.X1, Math.Max(bottom, b.Y0 + size * 1.8));
    }

    // ================= Imágenes existentes =================

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedImageCommand), nameof(ReplaceSelectedImageCommand))]
    private PageImageInfo? _selectedImage;

    private (byte[] Bytes, double Aspect)? _pendingContentImage;

    /// <summary>Imagen bajo el punto (la más pequeña si se superponen).</summary>
    public PageImageInfo? HitTestImage(int pageIndex, double x, double y) =>
        ImagesOf(pageIndex).Where(i => i.Box.Inflate(1).Contains(x, y)).OrderBy(i => i.Box.Width * i.Box.Height).FirstOrDefault();

    public void SelectImage(PageImageInfo? image)
    {
        if (image is null && SelectedImage is null) return;
        if (image is not null) SelectAnnotation(null);
        SelectedImage = image;
        ShowOutline(image?.PageIndex, image?.Box, handles: true);
    }

    /// <summary>Tras editar, la imagen tiene otra caja: se vuelve a seleccionar la que quedó más cerca de la esperada.</summary>
    private void ReselectImageNear(int page, PdfRect expected)
    {
        var near = ImagesOf(page)
            .OrderBy(i => Math.Abs(i.Box.CenterX - expected.CenterX) + Math.Abs(i.Box.CenterY - expected.CenterY))
            .FirstOrDefault();
        SelectImage(near);
    }

    public void MoveSelectedImage(double dx, double dy)
    {
        if (SelectedImage is not { } img || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)) return;
        var b = img.Box;
        ChangeImageBox(img, new PdfRect(b.X0 + dx, b.Y0 + dy, b.X1 + dx, b.Y1 + dy));
    }

    public void ResizeSelectedImage(PdfRect newBox)
    {
        if (SelectedImage is not { } img || newBox.Width < 8 || newBox.Height < 8) return;
        ChangeImageBox(img, newBox);
    }

    private void ChangeImageBox(PageImageInfo img, PdfRect box)
    {
        SelectImage(null);
        if (Edit(ed => ed.MoveImage(img, box), false, img.PageIndex)) ReselectImageNear(img.PageIndex, box);
    }

    private bool HasSelectedImage() => SelectedImage is not null && CanEdit;

    [RelayCommand(CanExecute = nameof(HasSelectedImage))]
    private void DeleteSelectedImage()
    {
        if (SelectedImage is not { } img) return;
        SelectImage(null);
        Edit(ed => ed.DeleteImage(img), false, img.PageIndex);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedImage))]
    private void ReplaceSelectedImage()
    {
        if (SelectedImage is not { } img) return;
        var (bytes, _) = PickImageFile("Elegir la imagen nueva") ?? default;
        if (bytes is null) return;
        SelectImage(null);
        if (Edit(ed => ed.ReplaceImage(img, bytes), false, img.PageIndex)) ReselectImageNear(img.PageIndex, img.Box);
    }

    private static (byte[] Bytes, double Aspect)? PickImageFile(string title)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = title,
        };
        if (dlg.ShowDialog() != true) return null;
        try
        {
            return StampFactory.LoadImage(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo leer la imagen:\n{ex.Message}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    // ================= Agregar una imagen al contenido de la página =================

    private bool PickPageImage()
    {
        _pendingContentImage = PickImageFile("Elegir la imagen a insertar");
        if (_pendingContentImage is null) return false;
        SearchStatus = "Haz clic en la página donde colocar la imagen";
        return true;
    }

    /// <summary>Inserta la imagen elegida con su esquina superior izquierda en el punto indicado.</summary>
    public void PlaceContentImage(PageViewModel page, PdfPoint at)
    {
        if (_pendingContentImage is not { } img) return;
        double width = Math.Min(160, page.PageWidth * 0.4);
        double height = width / img.Aspect;
        double x = Math.Clamp(at.X, 0, Math.Max(0, page.PageWidth - width));
        double y = Math.Clamp(at.Y, 0, Math.Max(0, page.PageHeight - height));
        var box = new PdfRect(x, y, x + width, y + height);
        if (!Edit(ed => ed.AddImage(page.Index, box, img.Bytes), false, page.Index)) return;

        Tool = AnnotTool.EditObjects; // se queda lista para moverla o redimensionarla
        ReselectImageNear(page.Index, box);
    }
}
