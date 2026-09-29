using DocsDR.App.Services;
using Velopack;

namespace DocsDR.App;

public static class Program
{
    /// <summary>
    /// Velopack debe ejecutarse antes que cualquier otra cosa: atiende los argumentos que le pasa el
    /// instalador (instalar, actualizar, desinstalar) y en esos casos termina el proceso.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => DefaultReader.Unregister())
            .Run();

        // Una sola ventana: abrir otro PDF desde el Explorador lo añade como pestaña a la ventana que ya está abierta.
        if (!SingleInstance.TryBecomePrimary()
            && SingleInstance.SendToPrimary(args.Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))))
            return;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
