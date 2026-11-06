using System.Diagnostics;
using System.Text;

namespace Lince.Compiler.Ejecucion;

public sealed record ResultadoEjecucion(int CodigoDeSalida, string Salida, string SalidaDeError);

/// <summary>
/// Ejecuta un ensamblado compilado con <c>dotnet &lt;ruta.dll&gt;</c>, en un proceso aparte.
/// Un proceso aparte, y no cargarlo en el del compilador, para que el programa del usuario
/// tenga su propia consola, su propio código de salida y no pueda dejar el compilador en un
/// estado raro.
/// </summary>
public static class Ejecutor
{
    /// <summary>Corre el programa compartiendo la consola actual (para la línea de comandos).</summary>
    public static int Ejecutar(string rutaEnsamblado)
    {
        using var proceso = Process.Start(new ProcessStartInfo("dotnet", [rutaEnsamblado]) { UseShellExecute = false })
            ?? throw new InvalidOperationException("No se pudo iniciar 'dotnet'.");
        proceso.WaitForExit();
        return proceso.ExitCode;
    }

    /// <summary>Corre el programa capturando su salida (para las pruebas).</summary>
    public static ResultadoEjecucion EjecutarCapturando(string rutaEnsamblado, TimeSpan? limite = null)
    {
        var inicio = new ProcessStartInfo("dotnet", [rutaEnsamblado])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proceso = Process.Start(inicio) ?? throw new InvalidOperationException("No se pudo iniciar 'dotnet'.");
        var salida = proceso.StandardOutput.ReadToEndAsync();
        var error = proceso.StandardError.ReadToEndAsync();

        if (!proceso.WaitForExit((int)(limite ?? TimeSpan.FromSeconds(30)).TotalMilliseconds))
        {
            proceso.Kill(entireProcessTree: true);
            throw new TimeoutException($"El programa '{rutaEnsamblado}' no terminó a tiempo.");
        }

        return new ResultadoEjecucion(proceso.ExitCode, salida.Result, error.Result);
    }
}
