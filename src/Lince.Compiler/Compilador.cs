using System.Collections.Immutable;
using System.Text;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Emision;
using Lince.Compiler.Enlace;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Compiler;

/// <summary>Lo que devuelve una compilación: los diagnósticos y, si no hubo errores, la ruta del ensamblado.</summary>
public sealed record ResultadoCompilacion(ImmutableArray<Diagnostico> Diagnosticos, string? RutaEnsamblado)
{
    public bool Exito => RutaEnsamblado is not null;

    public IEnumerable<Diagnostico> Errores => Diagnosticos.Where(d => d.EsError);
}

/// <summary>
/// Fachada del compilador: encadena léxico, sintaxis, enlace y emisión. Es lo que usan la
/// línea de comandos y las pruebas de extremo a extremo.
/// </summary>
public static class Compilador
{
    /// <summary>Solo las fases de análisis, sin emitir. Para herramientas y pruebas del enlazador.</summary>
    public static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(ArchivoFuente fuente)
    {
        var arbol = ArbolSintaxis.Analizar(fuente);
        var diagnosticos = new BolsaDiagnosticos();
        diagnosticos.AgregarTodos(arbol.Diagnosticos);
        var programa = Enlazador.Enlazar(arbol, diagnosticos);
        return (programa, diagnosticos.ToImmutableArray());
    }

    /// <summary>
    /// Compila el archivo y escribe <c>&lt;nombre&gt;.dll</c> y su <c>runtimeconfig.json</c> en el
    /// directorio de salida. El ensamblado se ejecuta con <c>dotnet &lt;nombre&gt;.dll</c>.
    /// </summary>
    public static ResultadoCompilacion Compilar(ArchivoFuente fuente, string directorioSalida, string? nombreEnsamblado = null)
    {
        var (programa, diagnosticos) = Enlazar(fuente);
        if (diagnosticos.Any(d => d.EsError))
        {
            return new ResultadoCompilacion(diagnosticos, null);
        }

        nombreEnsamblado ??= NombreDesdeRuta(fuente.Ruta);

        byte[] bytes;
        try
        {
            bytes = EmisorIl.Emitir(programa, nombreEnsamblado);
        }
        catch (Exception excepcion)
        {
            var bolsa = new BolsaDiagnosticos();
            bolsa.AgregarTodos(diagnosticos);
            bolsa.Error(Codigos.ErrorDeEmision, new Ubicacion(fuente, 0, 0),
                $"falló la emisión del ensamblado: {excepcion.GetType().Name}: {excepcion.Message}");
            return new ResultadoCompilacion(bolsa.ToImmutableArray(), null);
        }

        Directory.CreateDirectory(directorioSalida);
        var rutaDll = Path.Combine(directorioSalida, nombreEnsamblado + ".dll");
        File.WriteAllBytes(rutaDll, bytes);
        File.WriteAllText(Path.Combine(directorioSalida, nombreEnsamblado + ".runtimeconfig.json"), ConfiguracionDeRuntime(), new UTF8Encoding(false));

        return new ResultadoCompilacion(diagnosticos, rutaDll);
    }

    /// <summary>
    /// El ensamblado referencia la librería base del runtime con el que se compiló, así que el
    /// runtimeconfig pide ese mismo runtime (con la política de avance de versión por defecto).
    /// </summary>
    private static string ConfiguracionDeRuntime()
    {
        var mayor = Environment.Version.Major;
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{mayor}}.0",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{mayor}}.0.0"
                }
              }
            }
            """;
    }

    private static string NombreDesdeRuta(string ruta)
    {
        var nombre = Path.GetFileNameWithoutExtension(ruta);
        if (string.IsNullOrWhiteSpace(nombre) || nombre.StartsWith('<'))
        {
            return "programa";
        }

        var limpio = new string(nombre.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
        return limpio;
    }
}
