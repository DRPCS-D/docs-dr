using System.IO;
using DocsDR.App.ViewModels;
using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;

namespace DocsDR.App.Tests;

public class EditingViewModelTests
{
    /// <summary>PDF de <paramref name="pages"/> páginas; la página i mide 200+i*10 de ancho para identificarla.</summary>
    private static string CreatePdf(int pages)
    {
        var path = Path.Combine(Path.GetTempPath(), "docsdr-app-tests", $"{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (MuPdfDocument.NativeLock)
        {
            var d = new Document();
            for (int i = 0; i < pages; i++)
                d.NewPage(width: 200 + i * 10, height: 300).InsertText(new Point(20, 50), $"Hola mundo pagina {i + 1}", fontSize: 12);
            d.Save(path);
            d.Close();
        }
        return path;
    }

    private static (DocumentViewModel Vm, string Path) Open(int pages)
    {
        var path = CreatePdf(pages);
        return (new DocumentViewModel(new MuPdfService().Open(path)), path);
    }

    private static double[] Widths(DocumentViewModel vm) => vm.Pages.Select(p => p.PageWidth).ToArray();

    [Fact]
    public void Edit_marks_modified_and_supports_undo_redo()
    {
        var (vm, _) = Open(1);
        Assert.False(vm.IsModified);
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.AddShape(vm.Pages[0], AnnotationKind.Rectangle, new(20, 100), new(120, 160));

        Assert.True(vm.IsModified);
        Assert.StartsWith("● ", vm.DisplayTitle);
        Assert.Single(vm.Annotations);
        Assert.Equal(AnnotationKind.Rectangle, vm.SelectedAnnotation?.Kind); // la nueva queda seleccionada

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Annotations);
        Assert.False(vm.IsModified);
        Assert.True(vm.RedoCommand.CanExecute(null));

        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Annotations);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void Save_clears_modified_flag_and_persists_annotations()
    {
        var (vm, path) = Open(1);
        vm.AddShape(vm.Pages[0], AnnotationKind.Ellipse, new(20, 100), new(120, 160));

        Assert.True(vm.Save());

        Assert.False(vm.IsModified);
        vm.Dispose();
        using var reopened = new MuPdfService().Open(path);
        var ed = Assert.IsAssignableFrom<IPdfEditor>(reopened);
        Assert.Equal(AnnotationKind.Ellipse, Assert.Single(ed.GetAnnotations(0)).Kind);
    }

    [Fact]
    public void Undo_past_a_save_marks_document_as_modified_again()
    {
        var (vm, _) = Open(1);
        vm.AddShape(vm.Pages[0], AnnotationKind.Rectangle, new(20, 100), new(120, 160));
        vm.Save();

        vm.UndoCommand.Execute(null);

        Assert.True(vm.IsModified); // el archivo en disco todavía tiene la anotación
    }

    [Fact]
    public void Page_commands_rotate_insert_and_move_with_undo()
    {
        var (vm, _) = Open(3); // anchos 200, 210, 220

        vm.GoToPage(1);
        vm.RotateRightCommand.Execute(null); // gira la página 2 (210x300 → 300x210)
        Assert.Equal([200, 300, 220], Widths(vm));

        vm.InsertBlankPageCommand.Execute(null); // tras la página actual, con su mismo tamaño
        Assert.Equal(4, vm.PageCount);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Equal([200, 210, 220], Widths(vm));

        vm.MovePages([0], 2); // la primera pasa al final (después de la tercera)
        Assert.Equal([210, 220, 200], Widths(vm));

        vm.MovePages([2], 0); // y vuelve al principio
        Assert.Equal([200, 210, 220], Widths(vm));
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void Applying_a_markup_tool_with_selected_text_creates_a_highlight_and_clears_selection()
    {
        var (vm, _) = Open(1);
        var page = vm.Pages[0];
        vm.SetSelection(page, 0, 1); // "Hola mundo"
        Assert.True(vm.HasSelection);

        vm.SetToolCommand.Execute(AnnotTool.Highlight);

        Assert.False(vm.HasSelection);
        var highlight = Assert.Single(vm.Annotations).Info;
        Assert.Equal(AnnotationKind.Highlight, highlight.Kind);
        Assert.True(highlight.IsMarkup);
        Assert.Equal(AnnotTool.Highlight, vm.Tool); // la herramienta sigue activa
    }

    [Fact]
    public void Deleting_selected_annotation_is_undoable()
    {
        var (vm, _) = Open(1);
        vm.AddShape(vm.Pages[0], AnnotationKind.Line, new(20, 100), new(120, 100));
        Assert.True(vm.DeleteSelectedAnnotationCommand.CanExecute(null));

        vm.DeleteSelectedAnnotationCommand.Execute(null);
        Assert.Empty(vm.Annotations);

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.Annotations);
    }

    [Fact]
    public void Hit_test_finds_shapes_and_ignores_markup_unless_requested()
    {
        var (vm, _) = Open(1);
        var page = vm.Pages[0];
        vm.AddShape(page, AnnotationKind.Rectangle, new(100, 200), new(180, 260));
        vm.SetSelection(page, 0, 0);
        vm.SetToolCommand.Execute(AnnotTool.Highlight); // resalta "Hola" (cerca de x=20..45, y=40..52)

        Assert.Equal(AnnotationKind.Rectangle, vm.HitTestAnnotation(0, 140, 230, includeMarkup: false)?.Kind);
        Assert.Null(vm.HitTestAnnotation(0, 30, 46, includeMarkup: false));
        Assert.Equal(AnnotationKind.Highlight, vm.HitTestAnnotation(0, 30, 46, includeMarkup: true)?.Kind);
        Assert.Null(vm.HitTestAnnotation(0, 5, 290, includeMarkup: true));
    }

    [Fact]
    public void Undo_history_is_capped()
    {
        var (vm, _) = Open(1);
        for (int i = 0; i < 40; i++)
            vm.AddShape(vm.Pages[0], AnnotationKind.Rectangle, new(10 + i, 100), new(30 + i, 130));

        int undone = 0;
        while (vm.UndoCommand.CanExecute(null)) { vm.UndoCommand.Execute(null); undone++; }

        Assert.Equal(30, undone); // MaxUndoEntries
        Assert.Equal(10, vm.Annotations.Count);
    }
}
