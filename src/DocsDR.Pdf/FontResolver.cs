using DocsDR.Core;

namespace DocsDR.Pdf;

/// <summary>
/// Elige la fuente con la que se escribe un texto. Si cabe en Latin-1 se usan las 14 fuentes estándar
/// (Helvetica, Times, Courier), que no se incrustan y todos los lectores tienen. Si hay caracteres fuera de
/// Latin-1 (€, comillas tipográficas, otros alfabetos…) se incrusta la fuente equivalente de Windows.
/// </summary>
internal static class FontResolver
{
    // Archivos de Windows por familia: normal, negrita, cursiva, negrita cursiva.
    private static readonly Dictionary<string, string[]> WindowsFiles = new()
    {
        [TextFormat.Sans] = ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"],
        [TextFormat.Serif] = ["times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"],
        [TextFormat.Mono] = ["cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"],
    };

    private static readonly Dictionary<string, string[]> Standard14 = new()
    {
        [TextFormat.Sans] = ["helv", "hebo", "heit", "hebi"],
        [TextFormat.Serif] = ["tiro", "tibo", "tiit", "tibi"],
        [TextFormat.Mono] = ["cour", "cobo", "coit", "cobi"],
    };

    /// <summary>Devuelve (archivo de fuente o null, nombre de fuente).</summary>
    public static (string? File, string Name) Resolve(TextFormat format, string text)
    {
        var family = WindowsFiles.ContainsKey(format.Family) ? format.Family : TextFormat.Sans;
        int variant = (format.Bold ? 1 : 0) + (format.Italic ? 2 : 0);
        var standard = Standard14[family][variant];

        if (text.All(IsLatin1)) return (null, standard);

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), WindowsFiles[family][variant]);
        return File.Exists(path) ? (path, "DR" + Path.GetFileNameWithoutExtension(path)) : (null, standard);
    }

    /// <summary>Caracteres que las fuentes estándar pueden mostrar sin incrustar nada (incluye saltos de línea).</summary>
    private static bool IsLatin1(char c) => c is '\n' or '\r' or '\t' || (c >= 0x20 && c <= 0x7E) || (c >= 0xA0 && c <= 0xFF);
}
