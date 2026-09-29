using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocsDR.App.ViewModels;

namespace DocsDR.App.Services;

/// <summary>Genera la imagen PNG (fondo transparente) de un sello de texto.</summary>
public static class StampFactory
{
    public const double HeightPt = 34;
    private const double Scale = 4; // supersampling: nítido al ampliar

    /// <summary>Devuelve el PNG y su relación ancho/alto.</summary>
    public static (byte[] Png, double Aspect) Render(StampOption stamp)
    {
        var c = Color.FromRgb(stamp.Color.R, stamp.Color.G, stamp.Color.B);
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        var text = new FormattedText(stamp.ResolvedText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            28, brush, 1.0);

        const double pad = 10, border = 3;
        double w = text.Width + 2 * pad + 2 * border, h = text.Height + pad + 2 * border;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Scale, Scale));
            dc.DrawRoundedRectangle(null, new Pen(brush, border), new Rect(border / 2, border / 2, w - border, h - border), 6, 6);
            dc.DrawText(text, new Point(pad + border, (h - text.Height) / 2));
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * Scale), (int)Math.Ceiling(h * Scale), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return (Encode(bmp), w / h);
    }

    /// <summary>Lee una imagen del disco y devuelve sus bytes originales y su relación ancho/alto.</summary>
    public static (byte[] Bytes, double Aspect) LoadImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (bytes, (double)frame.PixelWidth / frame.PixelHeight);
    }

    private static byte[] Encode(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
