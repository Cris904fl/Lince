using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Enlace;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Fase 4a: importar espacios de .NET y usar sus tipos con los nombres originales.</summary>
public class PruebasDeInterop : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "lince-pruebas", Guid.NewGuid().ToString("N"));

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

    private static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(string codigo) =>
        Compilador.Enlazar(ArchivoFuente.DesdeTexto(codigo, "prueba.ln"));

    private static ImmutableArray<Diagnostico> Diagnosticos(string cuerpoDePrincipal, string encabezado = "") =>
        Enlazar($"{encabezado}\nfuncion principal() {{\n{cuerpoDePrincipal}\n}}").Diagnosticos;

    private static Diagnostico UnicoError(string cuerpoDePrincipal, string encabezado = "") =>
        Assert.Single(Diagnosticos(cuerpoDePrincipal, encabezado));

    private ResultadoEjecucion Ejecutar(string codigo, [System.Runtime.CompilerServices.CallerMemberName] string nombre = "")
    {
        var resultado = Compilador.Compilar(ArchivoFuente.DesdeTexto(codigo, nombre + ".ln"), _carpeta, nombre);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        return Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!);
    }

    private static string Lineas(params string[] lineas) => string.Concat(lineas.Select(l => l + Environment.NewLine));

    // ----- Sintaxis -----

    [Fact]
    public void Analiza_importar_con_partes_separadas_por_punto()
    {
        var arbol = ArbolSintaxis.Analizar("importar System.Text.RegularExpressions;\nimportar System.IO;\nfuncion principal() { }");

        Assert.Empty(arbol.Diagnosticos);
        Assert.Equal(["System.Text.RegularExpressions", "System.IO"], arbol.Raiz.Importaciones.Select(i => i.Espacio));
    }

    // ----- Enlace -----

    [Fact]
    public void Los_tipos_de_System_se_ven_sin_importar_y_los_demas_con_importar()
    {
        Assert.Empty(Diagnosticos("sea x = Math.Abs(-1); sea g = Guid.NewGuid();"));
        Assert.Empty(Diagnosticos("sea sb = nuevo StringBuilder();", "importar System.Text;"));

        var error = UnicoError("sea sb = nuevo StringBuilder();");
        Assert.Equal(Codigos.TipoNoExiste, error.Codigo);
    }

    [Fact]
    public void Un_espacio_que_no_existe_es_LN3060()
    {
        var error = UnicoError("", "importar System.Texto;");

        Assert.Equal(Codigos.EspacioNoExiste, error.Codigo);
        Assert.Contains("mayúscula", error.Mensaje);
    }

    [Fact]
    public void Los_tipos_de_dotnet_se_traducen_a_los_de_Lince_en_la_frontera()
    {
        var (programa, diagnosticos) = Enlazar("""
            importar System.Text;
            funcion principal() {
                sea a = Math.Max(1, 2);
                sea b = Math.Sqrt(4.0);
                sea c = "abc".IndexOf("b");
                sea d = nuevo StringBuilder();
                sea e = d.Length;
                sea f = Environment.NewLine;
                sea g = DateTime.Now;
                sea h = "x".ToUpper();
                sea i = Convert.ToBoolean(1);
            }
            """);

        Assert.Empty(diagnosticos);
        var tipos = programa.Funciones[0].Cuerpo.Sentencias.Cast<DeclaracionVariableEnlazada>().Select(d => d.Variable.Tipo.Nombre).ToList();
        Assert.Equal(["entero", "decimal", "entero", "StringBuilder", "entero", "texto", "DateTime", "texto", "logico"], tipos);
    }

    [Theory]
    [InlineData("sea x = Math;", Codigos.TipoComoValor)]
    [InlineData("imprimir(Math.Raiz(2.0));", Codigos.MiembroExternoNoExiste)]
    [InlineData("imprimir(Math.pi);", Codigos.MiembroExternoNoExiste)]
    [InlineData("imprimir(Math.Sqrt(\"dos\"));", Codigos.SobrecargaNoEncontrada)]
    [InlineData("imprimir(Math.Sqrt());", Codigos.SobrecargaNoEncontrada)]
    [InlineData("Math.PI = 3;", Codigos.MiembroDeSoloLectura)]
    [InlineData("sea n = 5; n.Dormir();", Codigos.MiembroNoExiste)]
    public void Los_errores_de_interoperabilidad_se_informan(string cuerpo, string codigo)
    {
        Assert.Equal(codigo, UnicoError(cuerpo).Codigo);
    }

    [Fact]
    public void El_mensaje_de_miembro_inexistente_sugiere_parecidos_y_recuerda_las_mayusculas()
    {
        var error = UnicoError("imprimir(Math.sqrt(2.0));");

        Assert.Contains("distinguen mayúsculas", error.Mensaje);
        Assert.Contains("Sqrt", error.Mensaje);
    }

    [Fact]
    public void Una_sobrecarga_que_no_encaja_lista_las_que_hay()
    {
        var error = UnicoError("imprimir(Math.Max(verdadero, falso));");

        Assert.Equal(Codigos.SobrecargaNoEncontrada, error.Codigo);
        Assert.Contains("Math.Max(", error.Mensaje);
        Assert.Contains("(logico, logico)", error.Mensaje);
    }

    [Fact]
    public void Un_tipo_de_dotnet_sirve_como_tipo_de_variable_parametro_y_retorno()
    {
        Assert.Empty(Diagnosticos("sea sb: StringBuilder = nuevo StringBuilder(); sea f = construir(sb); }\nfuncion construir(s: StringBuilder): StringBuilder { regresar s; ", "importar System.Text;"));
    }

    // ----- Extremo a extremo -----

    [Fact]
    public void Metodos_estaticos_de_instancia_propiedades_y_constructores_de_dotnet()
    {
        var resultado = Ejecutar("""
            importar System.Text;

            funcion principal() {
                imprimir(Math.Max(3, 7));
                imprimir(Math.Max(2.5, 1));
                imprimir(Math.Abs(-4));
                imprimir(Math.Pow(2, 10));
                imprimir(Math.PI.redondear());

                sea sb = nuevo StringBuilder();
                sb.Append("Hola");
                sb.Append(", ");
                sb.Append(42);
                sb.Append("!");
                imprimir(sb.ToString());
                imprimir(sb.Length);
                sb.Length = 4;
                imprimir(sb.ToString());

                sea con = nuevo StringBuilder("inicio");
                imprimir(con.Length);
            }
            """);

        Assert.Equal(Lineas("7", "2.5", "4", "1024", "3", "Hola, 42!", "9", "Hola", "6"), resultado.Salida);
    }

    [Fact]
    public void Estructuras_enumeraciones_y_constantes_de_dotnet()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                sea fecha = nuevo DateTime(2026, 11, 6);
                imprimir(fecha.Year + "-" + fecha.Month + "-" + fecha.Day);
                imprimir(fecha.DayOfWeek);
                imprimir(fecha.DayOfWeek == DayOfWeek.Friday);
                imprimir(fecha.AddDays(1).DayOfWeek != DayOfWeek.Friday);
                sea despues = fecha.AddMonths(2);
                imprimir(despues.Month);
                imprimir(DateTime.MinValue.Year);
                imprimir(fecha.ToString("yyyy/MM/dd"));
                imprimir(Int64.MaxValue);
            }
            """);

        Assert.Equal(Lineas("2026-11-6", "Friday", "verdadero", "verdadero", "1", "1", "2026/11/06", "9223372036854775807"), resultado.Salida);
    }

    [Fact]
    public void Archivos_y_expresiones_regulares_de_dotnet()
    {
        var resultado = Ejecutar("""
            importar System.IO;
            importar System.Text.RegularExpressions;

            funcion principal() {
                sea ruta = Path.Combine(Path.GetTempPath(), "lince-prueba-interop.txt");
                File.WriteAllText(ruta, "uno\ndos\ntres");
                sea lineas = File.ReadAllLines(ruta);
                imprimir(lineas.Length);
                imprimir(File.ReadAllText(ruta).partir("\n")[1]);
                File.Delete(ruta);
                imprimir(File.Exists(ruta));

                sea patron = nuevo Regex("[0-9]+");
                imprimir(patron.IsMatch("pedido 42"));
                imprimir(patron.Match("pedido 42").Value);
                imprimir(Regex.Replace("a1b22c", "[0-9]", "#"));
            }
            """);

        Assert.Equal(Lineas("3", "dos", "falso", "verdadero", "42", "a#b##c"), resultado.Salida);
    }

    [Fact]
    public void Los_tipos_basicos_exponen_los_miembros_de_dotnet_y_el_char_se_ve_como_texto()
    {
        var resultado = Ejecutar("""
            importar System.Globalization;

            funcion principal() {
                imprimir("lince".ToUpper());
                imprimir("a,b,c".Split(",").Length);
                imprimir("hola".Contains("ol"));
                imprimir("  x ".Trim().Length);
                imprimir("abc".IndexOf("c"));
                imprimir("x".PadLeft(3, "-"));
                imprimir((255).ToString("X"));
                imprimir((3.14159).ToString("F2", CultureInfo.InvariantCulture));
                sea numeros = [3, 1, 2];
                numeros.Sort();
                imprimir(numeros[0]);
                imprimir(numeros.Count);
            }
            """);

        Assert.Equal(Lineas("LINCE", "3", "verdadero", "1", "2", "--x", "FF", "3.14", "1", "3"), resultado.Salida);
    }

    [Fact]
    public void Un_valor_de_Lince_cabe_en_object_y_las_lambdas_entran_a_dotnet()
    {
        var resultado = Ejecutar("""
            clase Caja { valor: entero; }

            funcion principal() {
                imprimir(String.Format("{0} y {1}", 1, "dos"));
                imprimir(String.Join("-", ["a", "b"]));
                imprimir(Convert.ToInt32("12") + 1);
                sea caja = nuevo Caja(5);
                imprimir(Object.ReferenceEquals(caja, caja));
                sea numeros = [5, 2, 8];
                numeros.RemoveAll(n => n > 4);
                imprimir(numeros.cantidad);
                imprimir(numeros.Exists(n => n == 2));
            }
            """);

        Assert.Equal(Lineas("1 y dos", "a-b", "13", "verdadero", "1", "verdadero"), resultado.Salida);
    }
}
