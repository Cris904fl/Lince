using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Lince.Compiler;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Proyecto;
using Lince.Compiler.Texto;

namespace Lince.Cli;

/// <summary>
/// <c>lince compilar archivo.ln [--salida carpeta]</c> deja <c>archivo.dll</c> listo para
/// <c>dotnet archivo.dll</c>; <c>lince ejecutar archivo.ln</c> compila a una carpeta temporal
/// y lo corre. Si en la carpeta hay un <c>lince.json</c> (o se da la carpeta), se compila el
/// proyecto entero con sus paquetes. Códigos de salida: 0 bien, 1 errores de compilación,
/// 2 uso incorrecto; al ejecutar, el del programa.
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
                "restaurar" => Restaurar(args[1..]),
                "nuevo" => Nuevo(args[1..]),
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

        var carpetaBase = Directory.Exists(ruta) ? ruta! : Path.GetDirectoryName(Path.GetFullPath(ruta!))!;
        var resultado = CompilarRuta(ruta!, salida ?? Path.Combine(carpetaBase, "salida"));
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
            var resultado = CompilarRuta(ruta!, carpetaTemporal);
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

    private static int Restaurar(string[] args)
    {
        var carpeta = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var proyecto = Proyecto.Buscar(carpeta);
        if (proyecto is null)
        {
            Console.Error.WriteLine($"error: no hay un {Proyecto.NombreDeArchivo} en '{carpeta}'");
            return CodigoUsoIncorrecto;
        }

        RestaurarProyecto(proyecto);
        return 0;
    }

    private static ImmutableArray<string> RestaurarProyecto(Proyecto proyecto)
    {
        if (proyecto.Paquetes.Count == 0)
        {
            return [];
        }

        Console.WriteLine($"Restaurando los paquetes de '{proyecto.Nombre}':");
        var restaurador = new RestauradorNuGet(Console.Out);
        var dlls = restaurador.RestaurarAsync(proyecto).GetAwaiter().GetResult();
        Console.WriteLine($"Listo: {dlls.Length} {(dlls.Length == 1 ? "ensamblado" : "ensamblados")}.");
        return dlls;
    }

    private static int Nuevo(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("error: falta el nombre. Uso: lince nuevo <nombre>");
            return CodigoUsoIncorrecto;
        }

        var nombre = args[0];
        var carpeta = Path.Combine(Directory.GetCurrentDirectory(), nombre);
        if (File.Exists(Path.Combine(carpeta, Proyecto.NombreDeArchivo)))
        {
            Console.Error.WriteLine($"error: '{carpeta}' ya tiene un {Proyecto.NombreDeArchivo}");
            return CodigoUsoIncorrecto;
        }

        Proyecto.Crear(carpeta, nombre);
        Console.WriteLine($"Proyecto '{nombre}' creado en {carpeta}");
        Console.WriteLine($"Pruébelo con: lince ejecutar \"{carpeta}\"");
        return 0;
    }

    /// <summary>
    /// Compila un archivo suelto o, si hay un <c>lince.json</c> en su carpeta (o se dio la
    /// carpeta), el proyecto entero. Devuelve la ruta del ensamblado o null si hubo errores.
    /// </summary>
    private static string? CompilarRuta(string ruta, string directorioSalida)
    {
        var proyecto = Proyecto.Buscar(ruta);
        if (proyecto is null)
        {
            if (Directory.Exists(ruta))
            {
                Console.Error.WriteLine($"error LN4002: la carpeta '{ruta}' no tiene un {Proyecto.NombreDeArchivo}; cree uno con 'lince nuevo' o compile un archivo .ln");
                return null;
            }

            if (!File.Exists(ruta))
            {
                Console.Error.WriteLine($"error LN4002: no se encontró el archivo '{ruta}'");
                return null;
            }

            var fuente = ArchivoFuente.Leer(ruta);
            return Informar(Compilador.Compilar(fuente, directorioSalida), [fuente]);
        }

        var faltantes = proyecto.RutasDeArchivos.Where(r => !File.Exists(r)).ToList();
        if (faltantes.Count > 0)
        {
            Console.Error.WriteLine($"error LN4002: el proyecto nombra archivos que no existen: {string.Join(", ", faltantes)}");
            return null;
        }

        var referencias = RestauradorNuGet.YaRestauradas(proyecto) ?? RestaurarProyecto(proyecto);
        var fuentes = proyecto.RutasDeArchivos.Select(ArchivoFuente.Leer).ToList();
        return Informar(Compilador.Compilar(proyecto, directorioSalida, referencias), fuentes);
    }

    private static string? Informar(ResultadoCompilacion resultado, IReadOnlyList<ArchivoFuente> fuentes)
    {
        foreach (var diagnostico in resultado.Diagnosticos)
        {
            Console.Error.WriteLine(diagnostico);
            MostrarLineaConMarca(diagnostico.Ubicacion.Archivo, diagnostico.Linea, diagnostico.Columna);
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
                        return (null, null, $"error: '{comando}' recibe un solo archivo o carpeta");
                    }

                    ruta = args[i];
                    break;
            }
        }

        ruta ??= File.Exists(Path.Combine(Directory.GetCurrentDirectory(), Proyecto.NombreDeArchivo)) ? Directory.GetCurrentDirectory() : null;
        if (ruta is null)
        {
            return (null, null, $"error: falta el archivo o la carpeta. Uso: lince {comando} archivo.ln | carpeta");
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
              lince nuevo <nombre>                         Crea una carpeta con lince.json y un principal.ln.
              lince restaurar [carpeta]                    Descarga los paquetes NuGet del lince.json a la caché.
              lince compilar <archivo.ln | carpeta> [--salida <carpeta>]
                                                           Genera el .dll (y copia los paquetes) en salida/ o en la carpeta dada.
              lince ejecutar <archivo.ln | carpeta>        Compila y corre el programa.
              lince version                                Muestra la versión.
              lince ayuda                                  Muestra esta ayuda.

            Si el archivo está en una carpeta con lince.json, o se da la carpeta, se compila el proyecto
            entero: sus archivos, sus paquetes y sus marcos. Sin argumento, la carpeta actual.
            El ensamblado generado se ejecuta con: dotnet <nombre>.dll
            """);
        return 0;
    }
}
