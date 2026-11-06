using System.Collections.Immutable;
using System.Text;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Emision;
using Lince.Compiler.Enlace;
using Lince.Compiler.Proyecto;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Compiler;

/// <summary>Lo que devuelve una compilación: los diagnósticos y, si no hubo errores, la ruta del ensamblado.</summary>
public sealed record ResultadoCompilacion(ImmutableArray<Diagnostico> Diagnosticos, string? RutaEnsamblado)
{
    public bool Exito => RutaEnsamblado is not null;

    public IEnumerable<Diagnostico> Errores => Diagnosticos.Where(d => d.EsError);
}

/// <summary>Lo que una compilación necesita además de los archivos: las DLL de los paquetes y los marcos compartidos.</summary>
public sealed record OpcionesDeCompilacion(ImmutableArray<string> Referencias, ImmutableArray<string> Marcos)
{
    public static readonly OpcionesDeCompilacion Ninguna = new([], []);
}

/// <summary>
/// Fachada del compilador: encadena léxico, sintaxis, enlace y emisión. Es lo que usan la
/// línea de comandos y las pruebas de extremo a extremo.
/// </summary>
public static class Compilador
{
    /// <summary>Solo las fases de análisis, sin emitir. Para herramientas y pruebas del enlazador.</summary>
    public static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(ArchivoFuente fuente) =>
        Enlazar([fuente], OpcionesDeCompilacion.Ninguna);

    public static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(IEnumerable<ArchivoFuente> fuentes, OpcionesDeCompilacion opciones)
    {
        var arboles = fuentes.Select(ArbolSintaxis.Analizar).ToList();
        var diagnosticos = new BolsaDiagnosticos();
        foreach (var arbol in arboles)
        {
            diagnosticos.AgregarTodos(arbol.Diagnosticos);
        }

        var referencias = Externos.CargarReferencias(opciones.Referencias.Concat(opciones.Marcos.SelectMany(m => Proyecto.Marcos.EnsambladosDe(m))));
        var programa = Enlazador.Enlazar(arboles, diagnosticos, referencias);
        return (programa, diagnosticos.ToImmutableArray());
    }

    /// <summary>
    /// Compila el archivo y escribe <c>&lt;nombre&gt;.dll</c> y su <c>runtimeconfig.json</c> en el
    /// directorio de salida. El ensamblado se ejecuta con <c>dotnet &lt;nombre&gt;.dll</c>.
    /// </summary>
    public static ResultadoCompilacion Compilar(ArchivoFuente fuente, string directorioSalida, string? nombreEnsamblado = null) =>
        Compilar([fuente], directorioSalida, nombreEnsamblado ?? NombreDesdeRuta(fuente.Ruta), OpcionesDeCompilacion.Ninguna);

    /// <summary>
    /// Compila uno o varios archivos como un solo programa. Las DLL de los paquetes se copian
    /// junto al ensamblado para que el runtime las encuentre; los marcos van en el runtimeconfig.
    /// </summary>
    public static ResultadoCompilacion Compilar(IEnumerable<ArchivoFuente> fuentes, string directorioSalida, string nombreEnsamblado, OpcionesDeCompilacion opciones)
    {
        var listaDeFuentes = fuentes.ToList();
        var (programa, diagnosticos) = Enlazar(listaDeFuentes, opciones);
        if (diagnosticos.Any(d => d.EsError))
        {
            return new ResultadoCompilacion(diagnosticos, null);
        }

        byte[] bytes;
        try
        {
            bytes = EmisorIl.Emitir(programa, nombreEnsamblado);
        }
        catch (Exception excepcion)
        {
            var bolsa = new BolsaDiagnosticos();
            bolsa.AgregarTodos(diagnosticos);
            bolsa.Error(Codigos.ErrorDeEmision, new Ubicacion(listaDeFuentes[0], 0, 0),
                $"falló la emisión del ensamblado: {excepcion.GetType().Name}: {excepcion.Message}");
            return new ResultadoCompilacion(bolsa.ToImmutableArray(), null);
        }

        Directory.CreateDirectory(directorioSalida);
        var rutaDll = Path.Combine(directorioSalida, nombreEnsamblado + ".dll");
        File.WriteAllBytes(rutaDll, bytes);
        File.WriteAllText(Path.Combine(directorioSalida, nombreEnsamblado + ".runtimeconfig.json"), ConfiguracionDeRuntime(opciones.Marcos), new UTF8Encoding(false));

        foreach (var referencia in opciones.Referencias)
        {
            var destino = Path.Combine(directorioSalida, Path.GetFileName(referencia));
            if (!string.Equals(Path.GetFullPath(referencia), Path.GetFullPath(destino), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(referencia, destino, overwrite: true);
            }
        }

        return new ResultadoCompilacion(diagnosticos, rutaDll);
    }

    /// <summary>Compila un proyecto entero con sus paquetes ya restaurados.</summary>
    public static ResultadoCompilacion Compilar(Proyecto.Proyecto proyecto, string directorioSalida, ImmutableArray<string> referencias)
    {
        var fuentes = proyecto.RutasDeArchivos.Select(ArchivoFuente.Leer);
        return Compilar(fuentes, directorioSalida, NombreDesdeRuta(proyecto.Nombre), new OpcionesDeCompilacion(referencias, proyecto.Marcos));
    }

    /// <summary>
    /// El ensamblado referencia la librería base del runtime con el que se compiló, así que el
    /// runtimeconfig pide ese mismo runtime (con la política de avance de versión por defecto)
    /// y, si el proyecto los pide, los marcos compartidos.
    /// </summary>
    private static string ConfiguracionDeRuntime(ImmutableArray<string> marcos)
    {
        var mayor = Environment.Version.Major;
        var nombres = new[] { Proyecto.Marcos.NetCore }.Concat(marcos.Where(m => m != Proyecto.Marcos.NetCore)).Distinct();
        var entradas = nombres.Select(nombre => $$"""
                  {
                    "name": "{{nombre}}",
                    "version": "{{mayor}}.0.0"
                  }
            """);
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{mayor}}.0",
                "frameworks": [
            {{string.Join(",\n", entradas)}}
                ]
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
