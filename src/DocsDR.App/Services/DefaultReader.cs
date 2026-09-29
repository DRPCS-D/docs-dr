using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DocsDR.App.Services;

/// <summary>
/// Registra DOCS-DR en Windows como programa candidato para abrir archivos .pdf (sin permisos de administrador, solo HKCU).
/// Windows no deja que una aplicación se fije sola como predeterminada: el usuario la elige en Configuración.
/// </summary>
public static class DefaultReader
{
    private const string ProgId = "DocsDR.PDF";
    private const string AppKey = @"Software\DocsDR";
    private const string CapabilitiesKey = AppKey + @"\Capabilities";
    private const string RegisteredApps = @"Software\RegisteredApplications";
    private const string AppName = "DOCS-DR";

    /// <summary>Solo se registra la copia instalada (…\current\DocsDR.exe); las compilaciones de desarrollo no.</summary>
    public static string? InstalledExePath()
    {
        var path = Environment.ProcessPath;
        return path is not null && path.Contains(@"\current\", StringComparison.OrdinalIgnoreCase)
               && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "..", "Update.exe"))
            ? path
            : null;
    }

    /// <summary>Escribe (o corrige) el registro. Es idempotente y barato: se llama en cada inicio de la copia instalada.</summary>
    public static void Register(string exePath)
    {
        var command = $"\"{exePath}\" \"%1\"";
        using (var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            if (cmd?.GetValue(null) as string == command && Registry.CurrentUser.OpenSubKey(CapabilitiesKey) is not null) return;

        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            k.SetValue(null, "Documento PDF");
            k.SetValue("FriendlyTypeName", "Documento PDF (DOCS-DR)");
        }
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
            k.SetValue(null, $"\"{exePath}\",0");
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            k.SetValue(null, command);
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\DocsDR.exe\shell\open\command"))
            k.SetValue(null, command);

        using (var k = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            k.SetValue("ApplicationName", AppName);
            k.SetValue("ApplicationDescription", "Lector y editor de PDF, y convertidor de PDF a Excel");
        }
        using (var k = Registry.CurrentUser.CreateSubKey(CapabilitiesKey + @"\FileAssociations"))
            k.SetValue(".pdf", ProgId);
        using (var k = Registry.CurrentUser.CreateSubKey(RegisteredApps))
            k.SetValue(AppName, CapabilitiesKey);

        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Quita todo lo que escribió <see cref="Register"/> (al desinstalar).</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\DocsDR.exe", false);
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", true))
                k?.DeleteValue(ProgId, false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
            using (var k = Registry.CurrentUser.OpenSubKey(RegisteredApps, true))
                k?.DeleteValue(AppName, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* desinstalar no debe fallar por el registro */ }
    }

    /// <summary>Abre la pantalla «Aplicaciones predeterminadas» de Windows, ya situada en DOCS-DR si está registrada.</summary>
    public static void OpenWindowsSettings()
    {
        try { Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
