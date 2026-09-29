using DocsDR.App.ViewModels;
using DocsDR.Core;

namespace DocsDR.App.Tests;

public class SelectionTests
{
    /// <summary>Documento falso de una página con dos renglones: "Hola mundo" y "segunda linea".</summary>
    private sealed class FakeDoc : IPdfDocument
    {
        public string FilePath => "fake.pdf";
        public int PageCount => 1;
        public (double Width, double Height) GetPageSize(int pageIndex) => (600, 800);
        public RenderedPage Render(int pageIndex, double zoom) => new(1, 1, 3, new byte[3]);
        public IReadOnlyList<SearchHit> Search(string text) => [];
        public IReadOnlyList<OutlineItem> GetOutline() => [];
        public PageContent GetPageContent(int pageIndex, bool allowOcr = true) => throw new NotSupportedException();
        public void Dispose() { }

        public IReadOnlyList<TextWord> GetWords(int pageIndex) =>
        [
            new("Hola", new PdfRect(50, 100, 80, 112)),
            new("mundo", new PdfRect(85, 100, 125, 112)),
            new("segunda", new PdfRect(50, 120, 100, 132)),
            new("linea", new PdfRect(105, 120, 140, 132)),
        ];
    }

    private static (DocumentViewModel Doc, PageViewModel Page) Create()
    {
        var doc = new DocumentViewModel(new FakeDoc());
        return (doc, doc.Pages[0]);
    }

    [Fact]
    public void Selection_across_lines_inserts_newline_and_reverse_drag_works()
    {
        var (doc, page) = Create();

        doc.SetSelection(page, 3, 1); // arrastre hacia atrás

        Assert.Equal("mundo\nsegunda linea", doc.GetSelectedText());
        Assert.Equal(2, page.Selection.Count); // un rectángulo por renglón
    }

    [Fact]
    public void Selecting_one_word_and_a_whole_line()
    {
        var (doc, page) = Create();

        doc.SetSelection(page, 1, 1);
        Assert.Equal("mundo", doc.GetSelectedText());

        doc.SelectLine(page, 2);
        Assert.Equal("segunda linea", doc.GetSelectedText());
    }

    [Fact]
    public void Select_all_and_clear()
    {
        var (doc, page) = Create();

        doc.SelectAll(page);
        Assert.Equal("Hola mundo\nsegunda linea", doc.GetSelectedText());
        Assert.True(doc.HasSelection);

        doc.ClearSelection();
        Assert.False(doc.HasSelection);
        Assert.Empty(page.Selection);
        Assert.Equal("", doc.GetSelectedText());
    }

    [Fact]
    public void Nearest_word_respects_max_distance()
    {
        var (_, page) = Create();

        Assert.Equal(1, page.NearestWord(100, 106, 6));      // dentro de "mundo"
        Assert.Equal(0, page.NearestWord(46, 106, 6));       // 4 pt a la izquierda de "Hola"
        Assert.Equal(-1, page.NearestWord(300, 500, 6));     // lejos de todo
        Assert.True(page.NearestWord(300, 500, double.MaxValue) >= 0);  // al arrastrar siempre hay una palabra más cercana
    }
}
