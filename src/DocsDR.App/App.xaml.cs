using System.IO;
using System.Windows;
using System.Windows.Threading;
using DocsDR.App.Services;
using DocsDR.App.ViewModels;
using DocsDR.Core;
using DocsDR.Pdf;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DocsDR.App;

public partial class App : Application
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocsDR");

    public static IServiceProvider Services { get; private set; } = null!;

    // El tema debe fijarse antes de InitializeComponent (que carga los estilos de App.xaml): si se cambia después,
    // los estilos de los botones quedan basados en el tema clásico y se ven grises claros.
    public App() => ApplyTheme(new SettingsService().Theme);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(DataFolder);

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(DataFolder, "logs", "docsdr-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 10)
            .CreateLogger();
        DispatcherUnhandledException += OnUnhandledException;

        Services = new ServiceCollection()
            .AddSingleton<IPdfService, MuPdfService>()
            .AddSingleton<SettingsService>()
            .AddSingleton<UpdateService>()
            .AddSingleton<MainViewModel>()
            .AddTransient<MainWindow>()
            .BuildServiceProvider();

        var window = Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Asociación de archivos / "Abrir con": la ruta llega como argumento; si no hay ninguno se reabre la sesión anterior.
        var vm = Services.GetRequiredService<MainViewModel>();
        var toOpen = e.Args.Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a)).ToList();
        if (toOpen.Count == 0) vm.RestorePreviousSession();
        foreach (var arg in toOpen) vm.OpenPath(arg);
    }

    /// <summary>Cambia entre el tema claro y el oscuro (también en las ventanas ya abiertas).</summary>
#pragma warning disable WPF0001 // ThemeMode es la API oficial de temas de WPF (.NET 9+), aún marcada como experimental
    public static void ApplyTheme(string theme) =>
        Current.ThemeMode = theme == "Light" ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Error no controlado");
        MessageBox.Show(e.Exception.Message, "DOCS-DR — Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
