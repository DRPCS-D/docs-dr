using System.IO;
using System.Text.Json;

namespace DocsDR.App.Services;

/// <summary>Preferencias de usuario persistidas en %AppData%\DocsDR\settings.json.</summary>
public sealed class SettingsService
{
    private const int MaxRecent = 10;
    private static readonly string FilePath = Path.Combine(App.DataFolder, "settings.json");

    public List<string> RecentFiles { get; private set; } = [];

    /// <summary>Tema de la aplicación: "Dark" (por defecto) o "Light".</summary>
    public string Theme { get; private set; } = "Dark";

    public SettingsService()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                RecentFiles = data?.RecentFiles ?? [];
                Theme = data?.Theme == "Light" ? "Light" : "Dark";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "No se pudo leer la configuración");
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
        Save();
    }

    public void SetTheme(string theme)
    {
        Theme = theme == "Light" ? "Light" : "Dark";
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data { RecentFiles = RecentFiles, Theme = Theme }));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "No se pudo guardar la configuración");
        }
    }

    private sealed class Data
    {
        public List<string> RecentFiles { get; set; } = [];
        public string Theme { get; set; } = "Dark";
    }
}
