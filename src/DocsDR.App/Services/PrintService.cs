using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DocsDR.Core;

namespace DocsDR.App.Services;

public static class PrintService
{
    private const double PrintDpi = 200;

    public static void Print(IPdfDocument doc, string title)
    {
        var dlg = new PrintDialog { UserPageRangeEnabled = true, MinPage = 1, MaxPage = (uint)doc.PageCount };
        dlg.PageRange = new PageRange(1, doc.PageCount);
        if (dlg.ShowDialog() != true) return;

        int from = 1, to = doc.PageCount;
        if (dlg.PageRangeSelection == PageRangeSelection.UserPages)
        {
            from = Math.Max(1, dlg.PageRange.PageFrom);
            to = Math.Min(doc.PageCount, dlg.PageRange.PageTo);
        }
        var size = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        dlg.PrintDocument(new Paginator(doc, from - 1, to - 1, size), title);
    }

    /// <summary>Renderiza cada página a demanda para no tener todo el documento en memoria.</summary>
    private sealed class Paginator(IPdfDocument doc, int first, int last, Size pageSize) : DocumentPaginator
    {
        public override bool IsPageCountValid => true;
        public override int PageCount => last - first + 1;
        public override Size PageSize { get; set; } = pageSize;
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            int index = first + pageNumber;
            var (w, h) = doc.GetPageSize(index);
            var bmp = PageImages.ToBitmap(doc.Render(index, PrintDpi / 72.0));

            // Puntos PDF → DIP de WPF; se reduce para caber en la hoja (sin ampliar) y
            // se rota si la página es apaisada y la hoja vertical.
            double wd = w * 96 / 72, hd = h * 96 / 72;
            bool rotate = w > h && PageSize.Width < PageSize.Height;
            double scale = Math.Min(1, Math.Min(
                PageSize.Width / (rotate ? hd : wd),
                PageSize.Height / (rotate ? wd : hd)));
            double dw = wd * scale, dh = hd * scale;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (rotate)
                {
                    dc.PushTransform(new TranslateTransform(dh, 0));
                    dc.PushTransform(new RotateTransform(90));
                }
                dc.DrawImage(bmp, new Rect(0, 0, dw, dh));
            }
            return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
        }
    }
}
