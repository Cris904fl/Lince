using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Proyecto;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Fase 4b: lince.json, programas de varios archivos, marcos compartidos y paquetes NuGet.</summary>
public class PruebasDeProyecto : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "lince-pruebas", Guid.NewGuid().ToString("N"));

    public PruebasDeProyecto()
    {
        Directory.CreateDirectory(_carpeta);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Lineas(params string[] lineas) => string.Concat(lineas.Select(l => l + Environment.NewLine));

    private void Escribir(string nombre, string contenido) => File.WriteAllText(Path.Combine(_carpeta, nombre), contenido);

    [Fact]
    public void Lee_un_lince_json_con_todos_sus_campos()
    {
        Escribir(Proyecto.NombreDeArchivo, """
            {
              "nombre": "inventario",
              "version": "1.2.0",
              "archivos": ["principal.ln", "modelos.ln"],
              "paquetes": { "Newtonsoft.Json": "13.0.3" },
              "marcos": ["Microsoft.AspNetCore.App"]
            }
            """);

        var proyecto = Proyecto.Buscar(_carpeta)!;

        Assert.Equal("inventario", proyecto.Nombre);
        Assert.Equal("1.2.0", proyecto.Version);
        Assert.Equal(["principal.ln", "modelos.ln"], proyecto.Archivos.ToArray());
        Assert.Equal("13.0.3", proyecto.Paquetes["newtonsoft.json"]);
        Assert.Equal(["Microsoft.AspNetCore.App"], proyecto.Marcos.ToArray());
        Assert.Equal(Path.Combine(_carpeta, "modelos.ln"), proyecto.RutasDeArchivos[1]);
    }

    [Fact]
    public void Sin_lista_de_archivos_toma_todos_los_ln_de_la_carpeta_y_sin_nombre_el_de_la_carpeta()
    {
        Escribir(Proyecto.NombreDeArchivo, "{ }");
        Escribir("b.ln", "");
        Escribir("a.ln", "");

        var proyecto = Proyecto.Leer(Path.Combine(_carpeta, Proyecto.NombreDeArchivo));

        Assert.Equal(Path.GetFileName(_carpeta), proyecto.Nombre);
        Assert.Equal(["a.ln", "b.ln"], proyecto.RutasDeArchivos.Select(Path.GetFileName));
        Assert.Empty(proyecto.Paquetes);
    }

    [Fact]
    public void Buscar_encuentra_el_proyecto_desde_un_archivo_y_devuelve_null_si_no_hay()
    {
        Assert.Null(Proyecto.Buscar(_carpeta));
        Escribir(Proyecto.NombreDeArchivo, "{ \"nombre\": \"x\" }");
        Escribir("principal.ln", "");

        Assert.Equal("x", Proyecto.Buscar(Path.Combine(_carpeta, "principal.ln"))!.Nombre);
    }

    [Fact]
    public void Crear_deja_un_proyecto_listo_que_compila_y_corre()
    {
        var proyecto = Proyecto.Crear(Path.Combine(_carpeta, "demo"), "demo");

        Assert.True(File.Exists(proyecto.RutaDeArchivo));
        var resultado = Compilador.Compilar(proyecto, Path.Combine(_carpeta, "salida"), []);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        Assert.Equal(Lineas("Hola desde demo"), Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!).Salida);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("[1.2.3, )", "1.2.3")]
    [InlineData("[1.2.3]", "1.2.3")]
    [InlineData("(1.0, 2.0]", "1.0.0")]
    [InlineData("4.5.6.7", "4.5.6.7")]
    [InlineData("2.0.0-beta1", "2.0.0")]
    [InlineData("13", "13.0.0")]
    public void La_version_minima_de_un_rango_NuGet_se_lee_bien(string rango, string esperada)
    {
        Assert.Equal(esperada, RestauradorNuGet.TextoDe(RestauradorNuGet.VersionMinima(rango)));
    }

    [Fact]
    public void Un_programa_de_varios_archivos_comparte_funciones_tipos_e_importaciones()
    {
        var modelos = ArchivoFuente.DesdeTexto("""
            importar System.Text;
            clase Punto { x: entero; y: entero; funcion suma(): entero { regresar x + y; } }
            funcion armar(): StringBuilder { regresar nuevo StringBuilder("hola"); }
            """, "modelos.ln");
        var principal = ArchivoFuente.DesdeTexto("""
            funcion principal() {
                sea p = nuevo Punto(2, 3);
                imprimir(p.suma());
                imprimir(armar().Append("!").ToString());
            }
            """, "principal.ln");

        var resultado = Compilador.Compilar([principal, modelos], _carpeta, "varios", OpcionesDeCompilacion.Ninguna);

        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        Assert.Equal(Lineas("5", "hola!"), Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!).Salida);
    }

    [Fact]
    public void Una_funcion_duplicada_entre_archivos_se_informa_con_su_archivo()
    {
        var a = ArchivoFuente.DesdeTexto("funcion principal() { }", "a.ln");
        var b = ArchivoFuente.DesdeTexto("funcion principal() { }", "b.ln");

        var (_, diagnosticos) = Compilador.Enlazar([a, b], OpcionesDeCompilacion.Ninguna);

        var error = Assert.Single(diagnosticos);
        Assert.StartsWith("b.ln(1,9)", error.ToString());
    }

    [Fact]
    public void Un_marco_compartido_hace_visibles_sus_tipos_y_va_al_runtimeconfig()
    {
        if (Marcos.CarpetaDe("Microsoft.AspNetCore.App") is null)
        {
            return; // Sin ASP.NET Core instalado no hay nada que comprobar.
        }

        var fuente = ArchivoFuente.DesdeTexto("""
            importar Microsoft.AspNetCore.Http;
            funcion principal() {
                imprimir(StatusCodes.Status404NotFound);
            }
            """, "web.ln");

        var resultado = Compilador.Compilar([fuente], _carpeta, "web", new OpcionesDeCompilacion([], ["Microsoft.AspNetCore.App"]));

        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        Assert.Contains("Microsoft.AspNetCore.App", File.ReadAllText(Path.Combine(_carpeta, "web.runtimeconfig.json")));
        Assert.Equal(Lineas("404"), Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!).Salida);
    }

    [Fact]
    public async Task Un_paquete_NuGet_se_restaura_se_importa_y_se_copia_junto_al_programa()
    {
        Escribir(Proyecto.NombreDeArchivo, """{ "nombre": "conjson", "paquetes": { "Newtonsoft.Json": "13.0.3" } }""");
        Escribir("principal.ln", """
            importar Newtonsoft.Json;
            clase Punto { X: entero; Y: entero; }
            funcion principal() {
                imprimir(JsonConvert.SerializeObject(nuevo Punto(1, 2)));
            }
            """);
        var proyecto = Proyecto.Buscar(_carpeta)!;

        ImmutableArray<string> dlls;
        try
        {
            dlls = await new RestauradorNuGet().RestaurarAsync(proyecto);
        }
        catch (HttpRequestException)
        {
            return; // Sin red no se puede restaurar; la prueba no decide nada.
        }

        Assert.Contains(dlls, d => Path.GetFileName(d) == "Newtonsoft.Json.dll");
        Assert.Equal(dlls, RestauradorNuGet.YaRestauradas(proyecto));

        var salida = Path.Combine(_carpeta, "salida");
        var resultado = Compilador.Compilar(proyecto, salida, dlls);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        Assert.True(File.Exists(Path.Combine(salida, "Newtonsoft.Json.dll")));
        Assert.Equal(Lineas("{\"X\":1,\"Y\":2}"), Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!).Salida);
    }
}
