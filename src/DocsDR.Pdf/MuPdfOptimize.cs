using DocsDR.Core;
using MuPDF.NET;

namespace DocsDR.Pdf;

/// <summary>Reducción del tamaño del archivo: recompresión de imágenes y limpieza de objetos.</summary>
public sealed partial class MuPdfDocument
{
    public OptimizeResult OptimizeTo(string destPath, OptimizeLevel level)
    {
        byte[] current;
        lock (NativeLock) { current = _doc.Write(encryption: Constants.PDF_ENCRYPT_KEEP); }
        long original = File.Exists(FilePath) ? new System.IO.FileInfo(FilePath).Length : current.Length;

        var tmp = destPath + ".tmp";
        lock (NativeLock)
        {
            // Se trabaja sobre una copia: el documento abierto (y su historial de deshacer) no se toca.
            var copy = OpenBytes(current, _password);
            try
            {
                switch (level)
                {
                    case OptimizeLevel.Medium:
                        copy.RewriteImages(quality: 75, dpiThreshold: 170, dpiTarget: 150, lossy: true, lossless: true, bitonal: false, color: true, gray: true);
                        break;
                    case OptimizeLevel.Strong:
                        copy.RewriteImages(quality: 60, dpiThreshold: 110, dpiTarget: 96, lossy: true, lossless: true, bitonal: false, color: true, gray: true);
                        break;
                }
                // garbage 4: quita objetos sin uso y une duplicados; se comprimen flujos, imágenes y fuentes.
                copy.Save(tmp, garbage: 4, deflate: 1, deflateImages: 1, deflateFonts: 1, useObjstms: 1, encryption: Constants.PDF_ENCRYPT_KEEP);
            }
            finally { copy.Close(); }
        }
        File.Move(tmp, destPath, overwrite: true);
        return new OptimizeResult(original, new System.IO.FileInfo(destPath).Length);
    }
}
