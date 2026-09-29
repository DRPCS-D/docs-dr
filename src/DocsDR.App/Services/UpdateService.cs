using Velopack;
using Velopack.Sources;

namespace DocsDR.App.Services;

/// <summary>
/// Actualizaciones automáticas con Velopack, leyendo las versiones publicadas en GitHub Releases.
/// Solo funcionan en la copia instalada con el instalador de DOCS-DR.
/// </summary>
public sealed class UpdateService
{
    private UpdateManager? _manager;

    // Se crea al usarse: UpdateManager exige que VelopackApp.Run() (en Program.Main) ya se haya ejecutado.
    private UpdateManager Manager =>
        _manager ??= new UpdateManager(new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false));

    /// <summary>false si se está ejecutando desde el código fuente o una carpeta copiada a mano.</summary>
    public bool IsInstalled
    {
        get
        {
            try { return Manager.IsInstalled; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>Consulta si hay una versión más nueva. Devuelve null si ya se tiene la última.</summary>
    public Task<UpdateInfo?> CheckAsync() => Manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, Action<int>? progress = null) =>
        Manager.DownloadUpdatesAsync(update, progress);

    /// <summary>Cierra la aplicación, instala lo descargado y la vuelve a abrir.</summary>
    public void ApplyAndRestart(UpdateInfo update) => Manager.ApplyUpdatesAndRestart(update);

    public static string VersionOf(UpdateInfo update) => update.TargetFullRelease.Version.ToString();
}
