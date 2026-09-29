using System.IO;
using System.Text.Json;

namespace DocsDR.App.Services;

/// <summary>Un documento abierto al cerrar la app: se reabre en la misma página y zoom.</summary>
public sealed record SessionEntry(string Path, int Page, double Zoom);

/// <summary>Posición y tamaño de la ventana principal.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>Preferencias de usuario persistidas en %AppData%\DocsDR\settings.json.</summary>
public sealed class SettingsService
{
    private const int MaxRecent = 10;
    private static readonly string FilePath = Path.Combine(App.DataFolder, "settings.json");

    public List<string> RecentFiles { get; private set; } = [];

    /// <summary>Tema de la aplicación: "Dark" (por defecto) o "Light".</summary>
    public string Theme { get; private set; } = "Dark";

    /// <summary>¿Reabrir al iniciar los documentos que estaban abiertos al cerrar?</summary>
    public bool RestoreSession { get; private set; } = true;

    public List<SessionEntry> Session { get; private set; } = [];
    public int SessionSelected { get; private set; }
    public WindowPlacement? Window { get; private set; }

    /// <summary>Ancho del panel lateral de miniaturas (null = el predeterminado).</summary>
    public double? SidePanelWidth { get; private set; }

    public SettingsService()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                RecentFiles = data?.RecentFiles ?? [];
                Theme = data?.Theme == "Light" ? "Light" : "Dark";
                RestoreSession = data?.RestoreSession ?? true;
                Session = data?.Session ?? [];
                SessionSelected = data?.SessionSelected ?? 0;
                Window = data?.Window;
                SidePanelWidth = data?.SidePanelWidth;
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

    public void SetRestoreSession(bool value)
    {
        RestoreSession = value;
        Save();
    }

    /// <summary>Guarda cómo quedó la ventana y qué documentos había abiertos.</summary>
    public void SaveView(WindowPlacement window, IReadOnlyList<SessionEntry> session, int selected, double? sidePanelWidth)
    {
        Window = window;
        Session = session.ToList();
        SessionSelected = selected;
        SidePanelWidth = sidePanelWidth;
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data
            {
                RecentFiles = RecentFiles, Theme = Theme, RestoreSession = RestoreSession,
                Session = Session, SessionSelected = SessionSelected, Window = Window, SidePanelWidth = SidePanelWidth,
            }));
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
        public bool RestoreSession { get; set; } = true;
        public List<SessionEntry> Session { get; set; } = [];
        public int SessionSelected { get; set; }
        public WindowPlacement? Window { get; set; }
        public double? SidePanelWidth { get; set; }
    }
}
