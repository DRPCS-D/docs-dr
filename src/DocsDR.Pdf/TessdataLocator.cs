namespace DocsDR.Pdf;

/// <summary>
/// Localiza los datos de idioma de Tesseract (*.traineddata) usados por el OCR integrado de MuPDF.
/// Orden: variable TESSDATA_PREFIX, carpeta "tessdata" junto al ejecutable, %AppData%\DocsDR\tessdata.
/// </summary>
public static class TessdataLocator
{
    public static string UserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocsDR", "tessdata");

    public static (string Path, string Languages)? Find()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "tessdata"),
            UserFolder,
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var langs = new[] { "spa", "eng" }
                .Where(l => File.Exists(System.IO.Path.Combine(dir, l + ".traineddata")))
                .ToList();
            if (langs.Count > 0) return (dir, string.Join("+", langs));
        }
        return null;
    }
}
