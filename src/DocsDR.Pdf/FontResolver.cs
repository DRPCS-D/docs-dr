using DocsDR.Core;

namespace DocsDR.Pdf;

/// <summary>
/// Elige la fuente con la que se escribe un texto. Orden: una fuente equivalente instalada en Windows si se conoce
/// la fuente original (Calibri, Segoe UI, Georgia…); si no, la familia genérica: si el texto cabe en Latin-1 se usan
/// las 14 fuentes estándar (Helvetica, Times, Courier), que no se incrustan y todos los lectores tienen; si hay
/// caracteres fuera de Latin-1 (€, comillas tipográficas, otros alfabetos…) se incrusta la fuente equivalente de Windows.
/// (La reutilización de la fuente incrustada en el propio PDF la decide <see cref="MuPdfDocument"/>, que tiene el documento.)
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

    /// <summary>
    /// Fuentes de Windows que se reconocen por el nombre de la fuente original (sin espacios ni guiones, en minúsculas).
    /// Las más específicas van primero. Archivos: normal, negrita, cursiva, negrita cursiva (los que no existen se omiten).
    /// </summary>
    private static readonly (string Key, string[] Files)[] KnownFaces =
    [
        ("arialnarrow", ["arialn.ttf", "arialnb.ttf", "arialni.ttf", "arialnbi.ttf"]),
        ("arialblack", ["ariblk.ttf"]),
        ("segoeui", ["segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"]),
        ("calibri", ["calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"]),
        ("candara", ["candara.ttf", "candarab.ttf", "candarai.ttf", "candaraz.ttf"]),
        ("corbel", ["corbel.ttf", "corbelb.ttf", "corbeli.ttf", "corbelz.ttf"]),
        ("constantia", ["constan.ttf", "constanb.ttf", "constani.ttf", "constanz.ttf"]),
        ("consolas", ["consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"]),
        ("tahoma", ["tahoma.ttf", "tahomabd.ttf"]),
        ("verdana", ["verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf"]),
        ("georgia", ["georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf"]),
        ("trebuchet", ["trebuc.ttf", "trebucbd.ttf", "trebucit.ttf", "trebucbi.ttf"]),
        ("palatino", ["pala.ttf", "palab.ttf", "palai.ttf", "palabi.ttf"]),
        ("comicsans", ["comic.ttf", "comicbd.ttf", "comici.ttf", "comicz.ttf"]),
        ("impact", ["impact.ttf"]),
        ("lucidaconsole", ["lucon.ttf"]),
    ];

    private static readonly string[] FontFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
    ];

    /// <summary>Devuelve (archivo de fuente o null, nombre de fuente).</summary>
    public static (string? File, string Name) Resolve(TextFormat format, string text)
    {
        if (format.Face is { } face && MatchInstalled(face.Name, format.Bold, format.Italic) is { } match)
            return (match, "DR" + Path.GetFileNameWithoutExtension(match));

        var family = WindowsFiles.ContainsKey(format.Family) ? format.Family : TextFormat.Sans;
        int variant = (format.Bold ? 1 : 0) + (format.Italic ? 2 : 0);
        var standard = Standard14[family][variant];

        if (text.All(IsLatin1)) return (null, standard);

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), WindowsFiles[family][variant]);
        return File.Exists(path) ? (path, "DR" + Path.GetFileNameWithoutExtension(path)) : (null, standard);
    }

    /// <summary>Nombre de fuente sin el prefijo de subconjunto de PDF («ABCDEF+Calibri-Bold» → «Calibri-Bold»).</summary>
    public static string StripSubsetPrefix(string name) =>
        name.Length > 7 && name[6] == '+' && name.Take(6).All(char.IsAsciiLetterUpper) ? name[7..] : name;

    /// <summary>Archivo de una fuente instalada equivalente a la original (con el estilo pedido), o null si no se conoce.</summary>
    public static string? MatchInstalled(string faceName, bool bold, bool italic)
    {
        var key = new string(StripSubsetPrefix(faceName).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var (k, files) in KnownFaces)
        {
            if (!key.Contains(k)) continue;
            int variant = (bold ? 1 : 0) + (italic ? 2 : 0);
            // Si la familia no tiene esa variante (p. ej. Tahoma sin cursiva) se usa la más cercana que exista.
            int[] order = variant switch { 3 => [3, 1, 2, 0], 2 => [2, 0], 1 => [1, 0], _ => [0] };
            foreach (int v in order)
            {
                if (v >= files.Length) continue;
                foreach (var folder in FontFolders)
                {
                    var path = Path.Combine(folder, files[v]);
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }
        return null;
    }

    /// <summary>Caracteres que las fuentes estándar pueden mostrar sin incrustar nada (incluye saltos de línea).</summary>
    private static bool IsLatin1(char c) => c is '\n' or '\r' or '\t' || (c >= 0x20 && c <= 0x7E) || (c >= 0xA0 && c <= 0xFF);
}
