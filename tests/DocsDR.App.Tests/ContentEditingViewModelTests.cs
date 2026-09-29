using System.Globalization;
using System.IO;
using DocsDR.App.ViewModels;
using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;
using TextAlign = DocsDR.Core.TextAlign;

namespace DocsDR.App.Tests;

/// <summary>Edición de texto e imágenes existentes desde el ViewModel (con coma decimal, como en es-ES/es-PY).</summary>
public class ContentEditingViewModelTests : IDisposable
{
    private static readonly byte[] SolidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public ContentEditingViewModelTests() => CultureInfo.CurrentCulture = new CultureInfo("es-ES");

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    private static DocumentViewModel Open(bool withImage = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "docsdr-app-tests", $"{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var culture = CultureInfo.CurrentCulture;
        lock (MuPdfDocument.NativeLock)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var d = new Document();
                var p = d.NewPage(width: 400, height: 400);
                p.InsertText(new Point(30, 60), "Titulo en rojo", fontSize: 14, fontName: "hebo", color: [0.8f, 0f, 0f]);
                p.InsertText(new Point(30, 90), "Total", fontSize: 11.5f, fontName: "helv");
                p.InsertText(new Point(150, 90), "1.234,56", fontSize: 11.5f, fontName: "helv");
                if (withImage) p.InsertImage(new Rect(100, 150, 200, 210), stream: SolidPng, keepProportion: false);
                d.Save(path);
                d.Close();
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
        return new DocumentViewModel(new MuPdfService().Open(path));
    }

    private static PdfPoint CenterOf(DocumentViewModel vm, string text)
    {
        var line = ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == text);
        return new PdfPoint(line.Box.CenterX, line.Box.CenterY);
    }

    private static string[] Words(DocumentViewModel vm) => vm.Document.GetWords(0).Select(w => w.Text).ToArray();

    [Fact]
    public void Begin_edit_loads_the_original_format_into_the_toolbar_controls()
    {
        var vm = Open();

        var session = vm.BeginTextEdit(vm.Pages[0], CenterOf(vm, "Titulo en rojo"));

        Assert.NotNull(session);
        Assert.Equal("Titulo en rojo", session.Text);
        Assert.Equal(TextFormat.Sans, vm.TextFamily);
        Assert.True(vm.TextBold);
        Assert.False(vm.TextItalic);
        Assert.Equal(14, vm.TextSize, 1);
        Assert.Equal("Original", vm.TextColor!.Name); // el rojo del documento no está en la lista: se agrega
        Assert.InRange((int)vm.TextColor.Value.R, 0xCB, 0xCD);
        Assert.Contains(14, vm.TextSizes); // el tamaño 11.5 / 14 queda disponible
    }

    [Fact]
    public void Committing_changes_the_text_and_can_be_undone()
    {
        var vm = Open();
        var session = vm.BeginTextEdit(vm.Pages[0], CenterOf(vm, "Titulo en rojo"))!;
        Assert.False(vm.TextEditChanged(session, "Titulo en rojo")); // sin cambios: no hay nada que aplicar

        Assert.True(vm.TextEditChanged(session, "Título nuevo"));
        vm.CommitTextEdit(session, "Título nuevo");

        Assert.Contains("nuevo", Words(vm));
        Assert.DoesNotContain("rojo", Words(vm));
        Assert.Contains("Total", Words(vm)); // el resto queda intacto
        Assert.True(vm.IsModified);
        var edited = ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text.StartsWith("Título"));
        Assert.InRange((int)edited.Format.Color.R, 0xCB, 0xCD); // conserva el color rojo original
        Assert.True(edited.Format.Bold);

        vm.UndoCommand.Execute(null);
        Assert.Contains("rojo", Words(vm));
        Assert.DoesNotContain("nuevo", Words(vm));
    }

    [Fact]
    public void Changing_only_the_format_counts_as_a_change()
    {
        var vm = Open();
        var session = vm.BeginTextEdit(vm.Pages[0], CenterOf(vm, "Total"))!;

        vm.TextSize = 16;

        Assert.True(vm.TextEditChanged(session, "Total"));
        vm.CommitTextEdit(session, "Total");
        var line = ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == "Total");
        Assert.Equal(16, line.Format.Size, 1);
    }

    [Fact]
    public void Numbers_default_to_right_alignment_and_keep_their_right_edge()
    {
        var vm = Open();
        var line = ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == "1.234,56");
        var session = vm.BeginTextEdit(vm.Pages[0], CenterOf(vm, "1.234,56"))!;

        Assert.Equal(TextAlign.Right, vm.TextAlignment);
        vm.CommitTextEdit(session, "12.345.678,90"); // más ancho que el original

        var edited = ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == "12.345.678,90");
        Assert.InRange(edited.Box.X1, line.Box.X1 - 1.5, line.Box.X1 + 1.5); // el borde derecho no se mueve
        Assert.True(edited.Box.X0 < line.Box.X0);                             // crece hacia la izquierda
    }

    [Fact]
    public void Clicking_where_there_is_no_text_starts_nothing()
    {
        var vm = Open();

        Assert.Null(vm.BeginTextEdit(vm.Pages[0], new PdfPoint(300, 300)));
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void New_text_uses_the_current_format()
    {
        var vm = Open();
        vm.TextFamily = TextFormat.Serif;
        vm.TextSize = 13;
        vm.TextItalic = true;
        var session = vm.BeginNewText(vm.Pages[0], new PdfPoint(40, 250));

        vm.CommitTextEdit(session, "Nota al pie");

        var line = ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).Single(l => l.Text == "Nota al pie");
        Assert.Equal(TextFormat.Serif, line.Format.Family);
        Assert.True(line.Format.Italic);
        Assert.Equal(13, line.Format.Size, 1);
    }

    [Fact]
    public void Images_can_be_selected_moved_resized_and_deleted_with_undo()
    {
        var vm = Open(withImage: true);
        var image = vm.HitTestImage(0, 150, 180);
        Assert.NotNull(image);

        vm.SelectImage(image);
        Assert.NotNull(vm.SelectedImage);
        Assert.Null(vm.SelectedAnnotation);

        vm.MoveSelectedImage(50, 20);
        var moved = Assert.Single(((IPdfEditor)vm.Document).GetImages(0));
        Assert.InRange(moved.Box.X0, 149, 151);
        Assert.InRange(moved.Box.Y0, 169, 171);
        Assert.NotNull(vm.SelectedImage); // sigue seleccionada, en su nueva posición
        Assert.InRange(vm.SelectedImage!.Box.X0, 149, 151);

        vm.ResizeSelectedImage(new PdfRect(150, 170, 300, 260));
        Assert.InRange(Assert.Single(((IPdfEditor)vm.Document).GetImages(0)).Box.Width, 149, 151);

        Assert.True(vm.DeleteSelectedImageCommand.CanExecute(null));
        vm.DeleteSelectedImageCommand.Execute(null);
        Assert.Empty(((IPdfEditor)vm.Document).GetImages(0));
        Assert.Null(vm.SelectedImage);

        vm.UndoCommand.Execute(null);
        Assert.Single(((IPdfEditor)vm.Document).GetImages(0));
    }

    [Fact]
    public void Selecting_an_image_and_an_annotation_are_mutually_exclusive()
    {
        var vm = Open(withImage: true);
        vm.AddShape(vm.Pages[0], AnnotationKind.Rectangle, new(20, 300), new(80, 350));
        Assert.NotNull(vm.SelectedAnnotation);

        vm.SelectImage(vm.HitTestImage(0, 150, 180));
        Assert.Null(vm.SelectedAnnotation);
        Assert.NotNull(vm.SelectedImage);

        vm.SelectAnnotation(vm.Annotations[0].Info);
        Assert.Null(vm.SelectedImage);
    }

    [Fact]
    public void Leaving_the_image_tool_clears_the_image_selection()
    {
        var vm = Open(withImage: true);
        vm.Tool = AnnotTool.EditObjects;
        vm.SelectImage(vm.HitTestImage(0, 150, 180));
        Assert.NotNull(vm.SelectedImage);

        vm.Tool = AnnotTool.Select;

        Assert.Null(vm.SelectedImage);
    }

    [Fact]
    public void Tool_hint_describes_the_active_tool()
    {
        var vm = Open();

        vm.Tool = AnnotTool.EditText;
        Assert.Contains("renglón", vm.ToolHint);

        vm.ParagraphMode = true;
        Assert.Contains("párrafo", vm.ToolHint);

        vm.Tool = AnnotTool.EditObjects;
        Assert.Contains("imagen", vm.ToolHint);
    }
}
