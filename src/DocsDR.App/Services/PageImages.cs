using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocsDR.Core;

namespace DocsDR.App.Services;

public static class PageImages
{
    /// <summary>Factor de escala del monitor (1.0 = 96 DPI) para renderizar nítido en pantallas HiDPI.</summary>
    public static double DeviceScale
    {
        get
        {
            var source = Application.Current?.MainWindow is { } w ? PresentationSource.FromVisual(w) : null;
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
    }

    /// <summary>Renderiza una página fuera del hilo de UI y devuelve un bitmap congelado.</summary>
    public static Task<BitmapSource> RenderAsync(IPdfDocument doc, int pageIndex, double zoom)
    {
        double scale = zoom * DeviceScale;
        return Task.Run(() => ToBitmap(doc.Render(pageIndex, scale)));
    }

    public static BitmapSource ToBitmap(RenderedPage page)
    {
        var bmp = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Rgb24, null, page.Pixels, page.Stride);
        bmp.Freeze();
        return bmp;
    }
}
