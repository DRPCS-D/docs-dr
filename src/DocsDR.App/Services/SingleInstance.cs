using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DocsDR.App.Services;

/// <summary>
/// Una sola ventana de DOCS-DR por usuario: si se vuelve a abrir la aplicación (doble clic en otro PDF), el segundo proceso
/// envía las rutas al primero por una tubería con nombre y termina; el primero las abre como pestañas.
/// </summary>
public static class SingleInstance
{
    // Cada copia (instalada, portable, de desarrollo) tiene su propia ventana única: se distinguen por su carpeta.
    private static readonly string Suffix = Environment.UserName.Replace('\\', '_') + "." +
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..8];
    private static readonly string PipeName = "DocsDR.Open." + Suffix;
    private static Mutex? _mutex;

    /// <summary>True si este proceso es el primero (y debe seguir arrancando).</summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, @"Local\DocsDR.Single." + Suffix, out bool first);
        return first;
    }

    /// <summary>Entrega las rutas al proceso que ya está abierto. False si no responde (entonces se arranca normalmente).</summary>
    public static bool SendToPrimary(IEnumerable<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(4000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths) writer.WriteLine(p);
            writer.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Escucha a los procesos posteriores; <paramref name="onPaths"/> se llama en un hilo secundario (lista vacía = solo activar).</summary>
    public static void Listen(Action<IReadOnlyList<string>> onPaths, CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var paths = new List<string>();
                    while (await reader.ReadLineAsync(stop) is { } line)
                        if (line.Length > 0) paths.Add(line);
                    onPaths(paths);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Error en la tubería de instancia única");
                    await Task.Delay(500);
                }
            }
        }, stop);
    }
}
