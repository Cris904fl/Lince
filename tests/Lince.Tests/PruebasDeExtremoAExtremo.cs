using Lince.Compiler;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>
/// Compila un programa a un ensamblado real, lo corre con <c>dotnet</c> en un proceso
/// aparte y compara la salida. Es la prueba que da por hecha la fase 1.
/// </summary>
public class PruebasDeExtremoAExtremo : IDisposable
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

    private ResultadoEjecucion Ejecutar(string codigo, [System.Runtime.CompilerServices.CallerMemberName] string nombre = "")
    {
        var resultado = Compilador.Compilar(ArchivoFuente.DesdeTexto(codigo, nombre + ".ln"), _carpeta, nombre);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        Assert.True(File.Exists(resultado.RutaEnsamblado));
        Assert.True(File.Exists(Path.ChangeExtension(resultado.RutaEnsamblado, ".runtimeconfig.json")));
        return Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!);
    }

    private static string Lineas(params string[] lineas) => string.Concat(lineas.Select(l => l + Environment.NewLine));

    [Fact]
    public void Hola_mundo()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                imprimir("Hola, mundo");
            }
            """);

        Assert.Equal(Lineas("Hola, mundo"), resultado.Salida);
        Assert.Equal(0, resultado.CodigoDeSalida);
    }

    [Fact]
    public void Imprime_cada_tipo_y_la_linea_vacia()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                imprimir(42);
                imprimir(-7);
                imprimir(2.5);
                imprimir(1.0);
                imprimir(verdadero);
                imprimir(falso);
                imprimir("año y ñandú");
                imprimir();
                sea t: texto? = nulo;
                imprimir(t);
            }
            """);

        Assert.Equal(Lineas("42", "-7", "2.5", "1", "verdadero", "falso", "año y ñandú", "", ""), resultado.Salida);
    }

    [Fact]
    public void Fibonacci_recursivo_con_para()
    {
        var resultado = Ejecutar("""
            funcion fibonacci(n: entero): entero {
                si n < 2 {
                    regresar n;
                }
                regresar fibonacci(n - 1) + fibonacci(n - 2);
            }

            funcion principal() {
                para i en 0..10 {
                    imprimir("fib(" + i + ") = " + fibonacci(i));
                }
            }
            """);

        Assert.Equal(Lineas("fib(0) = 0", "fib(1) = 1", "fib(2) = 1", "fib(3) = 2", "fib(4) = 3",
            "fib(5) = 5", "fib(6) = 8", "fib(7) = 13", "fib(8) = 21", "fib(9) = 34"), resultado.Salida);
    }

    [Fact]
    public void Factorial_con_mientras_y_enteros_de_64_bits()
    {
        var resultado = Ejecutar("""
            funcion factorial(n: entero): entero {
                var resultado = 1;
                var i = 2;
                mientras i <= n {
                    resultado *= i;
                    i += 1;
                }
                regresar resultado;
            }

            funcion principal() {
                imprimir(factorial(20));
            }
            """);

        Assert.Equal(Lineas("2432902008176640000"), resultado.Salida);
    }

    [Fact]
    public void Aritmetica_entera_trunca_y_el_modulo_conserva_el_signo()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                imprimir(7 / 2);
                imprimir(-7 / 2);
                imprimir(7 % 3);
                imprimir(-7 % 3);
                imprimir(2 + 3 * 4 - (10 - 4) / 2);
            }
            """);

        Assert.Equal(Lineas("3", "-3", "1", "-1", "11"), resultado.Salida);
    }

    [Fact]
    public void Decimales_con_conversion_implicita_desde_entero()
    {
        var resultado = Ejecutar("""
            funcion mitad(x: decimal): decimal {
                regresar x / 2;
            }

            funcion principal() {
                imprimir(mitad(7));
                imprimir(1 / 4);
                imprimir(1 / 4.0);
                var acumulado: decimal = 0;
                para i en 1..4 {
                    acumulado += i * 0.5;
                }
                imprimir(acumulado);
                imprimir(3 == 3.0);
                imprimir(2.5 >= 2);
            }
            """);

        Assert.Equal(Lineas("3.5", "0", "0.25", "3", "verdadero", "verdadero"), resultado.Salida);
    }

    [Fact]
    public void Comparaciones_y_logicos_con_cortocircuito()
    {
        var resultado = Ejecutar("""
            funcion explota(): logico {
                imprimir("no debería evaluarse");
                regresar verdadero;
            }

            funcion principal() {
                imprimir(1 < 2 && 2 <= 2 && 3 > 2 && 3 >= 3);
                imprimir(1 == 1 && 1 != 2);
                imprimir(falso && explota());
                imprimir(verdadero || explota());
                imprimir(!(1 < 2));
                imprimir(!falso == verdadero);
                imprimir("a" == "a");
                imprimir("a" != "b");
            }
            """);

        Assert.Equal(Lineas("verdadero", "verdadero", "falso", "verdadero", "falso", "verdadero", "verdadero", "verdadero"), resultado.Salida);
    }

    [Fact]
    public void Si_sino_si_sino_elige_una_sola_rama()
    {
        var resultado = Ejecutar("""
            funcion clasificar(n: entero): texto {
                si n < 0 {
                    regresar "negativo";
                } sino si n == 0 {
                    regresar "cero";
                } sino si n < 10 {
                    regresar "pequeño";
                } sino {
                    regresar "grande";
                }
            }

            funcion principal() {
                imprimir(clasificar(-5));
                imprimir(clasificar(0));
                imprimir(clasificar(7));
                imprimir(clasificar(100));
            }
            """);

        Assert.Equal(Lineas("negativo", "cero", "pequeño", "grande"), resultado.Salida);
    }

    [Fact]
    public void Romper_y_continuar_en_mientras_y_en_para()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                var i = 0;
                var acumulado = "";
                mientras verdadero {
                    i += 1;
                    si i % 2 == 0 {
                        continuar;
                    }
                    si i > 7 {
                        romper;
                    }
                    acumulado = acumulado + i;
                }
                imprimir(acumulado);

                var suma = 0;
                para j en 0..100 {
                    si j == 5 {
                        romper;
                    }
                    si j == 1 {
                        continuar;
                    }
                    suma += j;
                }
                imprimir(suma);
            }
            """);

        Assert.Equal(Lineas("1357", "9"), resultado.Salida);
    }

    [Fact]
    public void Los_limites_del_para_se_evaluan_una_sola_vez()
    {
        var resultado = Ejecutar("""
            funcion limite(): entero {
                imprimir("límite");
                regresar 3;
            }

            funcion principal() {
                var cuenta = 0;
                para i en limite() - 3..limite() {
                    cuenta += 1;
                }
                imprimir(cuenta);
                para i en 5..2 {
                    imprimir("nunca");
                }
            }
            """);

        Assert.Equal(Lineas("límite", "límite", "3"), resultado.Salida);
    }

    [Fact]
    public void Recursion_mutua_entre_funciones_declaradas_en_cualquier_orden()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                imprimir(esPar(10));
                imprimir(esImpar(10));
            }

            funcion esPar(n: entero): logico {
                si n == 0 { regresar verdadero; }
                regresar esImpar(n - 1);
            }

            funcion esImpar(n: entero): logico {
                si n == 0 { regresar falso; }
                regresar esPar(n - 1);
            }
            """);

        Assert.Equal(Lineas("verdadero", "falso"), resultado.Salida);
    }

    [Fact]
    public void Los_ambitos_de_bloque_aislan_las_variables()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                var x = 1;
                {
                    var y = 10;
                    x += y;
                }
                {
                    var y = 20;
                    x += y;
                }
                imprimir(x);
            }
            """);

        Assert.Equal(Lineas("31"), resultado.Salida);
    }

    [Fact]
    public void El_valor_de_principal_es_el_codigo_de_salida()
    {
        var resultado = Ejecutar("""
            funcion principal(): entero {
                imprimir("saliendo con 3");
                regresar 3;
            }
            """);

        Assert.Equal(Lineas("saliendo con 3"), resultado.Salida);
        Assert.Equal(3, resultado.CodigoDeSalida);
    }

    [Fact]
    public void El_valor_de_una_llamada_usada_como_sentencia_se_descarta()
    {
        var resultado = Ejecutar("""
            funcion doble(n: entero): entero {
                imprimir("doble de " + n);
                regresar n * 2;
            }

            funcion principal() {
                doble(4);
                imprimir("listo");
            }
            """);

        Assert.Equal(Lineas("doble de 4", "listo"), resultado.Salida);
    }

    [Fact]
    public void Un_programa_con_errores_no_genera_ensamblado()
    {
        var resultado = Compilador.Compilar(ArchivoFuente.DesdeTexto("funcion principal() { sea a: entero = \"x\"; }", "mal.ln"), _carpeta);

        Assert.False(resultado.Exito);
        Assert.Null(resultado.RutaEnsamblado);
        Assert.Single(resultado.Errores);
        Assert.False(File.Exists(Path.Combine(_carpeta, "mal.dll")));
    }
}
