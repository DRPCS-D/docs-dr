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
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
