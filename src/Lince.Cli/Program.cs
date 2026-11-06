using System.Reflection;
using System.Text;
using Lince.Compiler;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Texto;

namespace Lince.Cli;

/// <summary>
/// <c>lince compilar archivo.ln [--salida carpeta]</c> deja <c>archivo.dll</c> listo para
/// <c>dotnet archivo.dll</c>; <c>lince ejecutar archivo.ln</c> compila a una carpeta temporal
/// y lo corre. Códigos de salida: 0 bien, 1 errores de compilación, 2 uso incorrecto; al
/// ejecutar, el del programa.
/// </summary>
public static class Program
{
    private const int CodigoErroresDeCompilacion = 1;
    private const int CodigoUsoIncorrecto = 2;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            MostrarAyuda();
            return CodigoUsoIncorrecto;
        }

        try
        {
            return args[0] switch
            {
                "compilar" => Compilar(args[1..]),
                "ejecutar" => Ejecutar(args[1..]),
                "version" or "--version" or "-v" => MostrarVersion(),
                "ayuda" or "--help" or "-h" => MostrarAyuda(),
                _ => Desconocido(args[0]),
            };
        }
        catch (Exception excepcion)
        {
            Console.Error.WriteLine($"error LN4001: el compilador falló de forma inesperada: {excepcion.Message}");
            return CodigoErroresDeCompilacion;
        }
    }

    private static int Compilar(string[] args)
    {
        var (ruta, salida, error) = LeerArgumentos(args, "compilar");
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return CodigoUsoIncorrecto;
        }

        var resultado = CompilarArchivo(ruta!, salida ?? Path.GetDirectoryName(Path.GetFullPath(ruta!))!);
        if (resultado is null)
        {
            return CodigoErroresDeCompilacion;
        }

        Console.WriteLine($"Compilado: {resultado}");
        Console.WriteLine($"Ejecute con: dotnet \"{resultado}\"");
        return 0;
    }

    private static int Ejecutar(string[] args)
    {
        var (ruta, _, error) = LeerArgumentos(args, "ejecutar");
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return CodigoUsoIncorrecto;
        }

        var carpetaTemporal = Path.Combine(Path.GetTempPath(), "lince", Guid.NewGuid().ToString("N"));
        try
        {
            var resultado = CompilarArchivo(ruta!, carpetaTemporal);
            return resultado is null ? CodigoErroresDeCompilacion : Ejecutor.Ejecutar(resultado);
        }
        finally
        {
            try
            {
                Directory.Delete(carpetaTemporal, recursive: true);
            }
            catch (IOException)
            {
                // Si el sistema la tiene bloqueada, se queda en la carpeta temporal; no es grave.
            }
        }
    }

    /// <summary>Compila y escribe los diagnósticos; devuelve la ruta del ensamblado o null si hubo errores.</summary>
    private static string? CompilarArchivo(string ruta, string directorioSalida)
    {
        if (!File.Exists(ruta))
        {
            Console.Error.WriteLine($"error LN4002: no se encontró el archivo '{ruta}'");
            return null;
        }

        var fuente = ArchivoFuente.Leer(ruta);
        var resultado = Compilador.Compilar(fuente, directorioSalida);

        foreach (var diagnostico in resultado.Diagnosticos)
        {
            Console.Error.WriteLine(diagnostico);
            MostrarLineaConMarca(fuente, diagnostico.Linea, diagnostico.Columna);
        }

        if (!resultado.Exito)
        {
            var errores = resultado.Errores.Count();
            Console.Error.WriteLine(errores == 1 ? "1 error; no se generó el ensamblado." : $"{errores} errores; no se generó el ensamblado.");
            return null;
        }

        return resultado.RutaEnsamblado;
    }

    private static void MostrarLineaConMarca(ArchivoFuente fuente, int linea, int columna)
    {
        if (linea < 1 || linea > fuente.CantidadDeLineas)
        {
            return;
        }

        var texto = fuente.TextoDeLinea(linea);
        Console.Error.WriteLine($"    {texto}");
        Console.Error.WriteLine($"    {new string(' ', Math.Max(0, columna - 1))}^");
    }

    private static (string? Ruta, string? Salida, string? Error) LeerArgumentos(string[] args, string comando)
    {
        string? ruta = null;
        string? salida = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--salida" or "-s":
                    if (i + 1 >= args.Length)
                    {
                        return (null, null, "error: '--salida' necesita una carpeta");
                    }

                    salida = args[++i];
                    break;

                case var opcion when opcion.StartsWith('-'):
                    return (null, null, $"error: opción desconocida '{opcion}'");

                default:
                    if (ruta is not null)
                    {
                        return (null, null, $"error: '{comando}' recibe un solo archivo");
                    }

                    ruta = args[i];
                    break;
            }
        }

        if (ruta is null)
        {
            return (null, null, $"error: falta el archivo. Uso: lince {comando} archivo.ln");
        }

        return (ruta, salida, null);
    }

    private static int Desconocido(string comando)
    {
        Console.Error.WriteLine($"error: comando desconocido '{comando}'");
        MostrarAyuda();
        return CodigoUsoIncorrecto;
    }

    private static int MostrarVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Console.WriteLine($"lince {version} (.NET {Environment.Version})");
        return 0;
    }

    private static int MostrarAyuda()
    {
        Console.WriteLine("""
            lince: compilador del lenguaje Lince

            Uso:
              lince compilar <archivo.ln> [--salida <carpeta>]   Genera <archivo>.dll junto al fuente (o en la carpeta dada).
              lince ejecutar <archivo.ln>                        Compila y corre el programa.
              lince version                                      Muestra la versión.
              lince ayuda                                        Muestra esta ayuda.

            El ensamblado generado se ejecuta con: dotnet <archivo>.dll
            """);
        return 0;
    }
}
