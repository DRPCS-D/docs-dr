using System.Diagnostics;
using System.Reflection;

namespace DocsDR.App.Services;

/// <summary>Datos de la aplicación que se muestran en Ayuda y usa el sistema de actualización.</summary>
public static class AppInfo
{
    public const string ProductName = "DOCS-DR";
    public const string Description = "Lector y editor de PDF, y convertidor de PDF a Excel para Windows.";
    public const string Company = "DRPCS E.A.S.";
    public const string Author = "Diago Rene Ruiz Diaz Rios";
    public const string SupportEmail = "diagorr@gmail.com";
    public const string RepositoryUrl = "https://github.com/DRPCS-D/docs-dr";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    public static string Copyright => $"© {DateTime.Now.Year} {Company}";

    /// <summary>Versión (ej. 1.0.0) tomada del ensamblado, sin el sufijo de compilación.</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(AppInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
            var v = asm.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string SupportMailto => $"mailto:{SupportEmail}?subject={Uri.EscapeDataString($"{ProductName} {Version}")}";

    /// <summary>Librerías de terceros incluidas y su licencia.</summary>
    public static IReadOnlyList<ThirdPartyLibrary> ThirdParty { get; } =
    [
        new("MuPDF / MuPDF.NET", "Artifex Software", "AGPL-3.0", "https://mupdf.com"),
        new("ClosedXML", "ClosedXML", "MIT", "https://github.com/ClosedXML/ClosedXML"),
        new("CommunityToolkit.Mvvm", "Microsoft", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Microsoft.Extensions.DependencyInjection", "Microsoft", "MIT", "https://github.com/dotnet/runtime"),
        new("Serilog", "Serilog Contributors", "Apache-2.0", "https://serilog.net"),
        new("Velopack", "Velopack Ltd", "MIT", "https://velopack.io"),
        new("Tesseract OCR (datos de idioma spa y eng)", "Tesseract OCR contributors", "Apache-2.0", "https://github.com/tesseract-ocr/tessdata_fast"),
        new(".NET y WPF", "Microsoft", "MIT", "https://dot.net"),
    ];

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "No se pudo abrir {Url}", url);
        }
    }
}

public sealed record ThirdPartyLibrary(string Name, string Owner, string License, string Url);
