using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Enlace;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Fase 3b: lambdas, tipos función, clausuras y los métodos de orden superior de las listas.</summary>
public class PruebasDeLambdas : IDisposable
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

    private static ImmutableArray<Diagnostico> Diagnosticos(string cuerpoDePrincipal, string declaraciones = "") =>
        Enlazar($"{declaraciones}\nfuncion principal() {{\n{cuerpoDePrincipal}\n}}").Diagnosticos;

    private static Diagnostico UnicoError(string cuerpoDePrincipal, string declaraciones = "") =>
        Assert.Single(Diagnosticos(cuerpoDePrincipal, declaraciones));

    private ResultadoEjecucion Ejecutar(string codigo, [System.Runtime.CompilerServices.CallerMemberName] string nombre = "")
    {
        var resultado = Compilador.Compilar(ArchivoFuente.DesdeTexto(codigo, nombre + ".ln"), _carpeta, nombre);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        return Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!);
    }

    private static string Lineas(params string[] lineas) => string.Concat(lineas.Select(l => l + Environment.NewLine));

    // ----- Sintaxis -----

    [Fact]
    public void Analiza_las_formas_de_lambda_y_los_tipos_funcion()
    {
        var arbol = ArbolSintaxis.Analizar("""
            funcion f(g: funcion(entero, texto): logico, h: funcion()): funcion(entero): entero {
                sea a = x => x * 2;
                sea b = (x, y) => x + y;
                sea c = (x: entero): entero => { regresar x; };
                sea d = () => 1;
                sea e = (x) + 1;
                regresar a;
            }
            """);

        Assert.Empty(arbol.Diagnosticos);
        var funcion = arbol.Raiz.Funciones[0];
        var g = funcion.Parametros[0].Tipo;
        Assert.True(g.EsFuncion);
        Assert.Equal(2, g.Argumentos.Length);
        Assert.Equal("logico", g.Retorno!.Palabra.Texto);
        Assert.Null(funcion.Parametros[1].Tipo.Retorno);
        Assert.True(funcion.TipoRetorno!.EsFuncion);

        var valores = funcion.Cuerpo.Sentencias.OfType<DeclaracionVariableSintaxis>().Select(s => s.Valor).ToList();
        var a = Assert.IsType<LambdaSintaxis>(valores[0]);
        Assert.Null(a.ParentesisAbre);
        Assert.Single(a.Parametros);
        Assert.NotNull(a.CuerpoExpresion);
        Assert.Equal(2, Assert.IsType<LambdaSintaxis>(valores[1]).Parametros.Length);
        var c = Assert.IsType<LambdaSintaxis>(valores[2]);
        Assert.NotNull(c.Parametros[0].Tipo);
        Assert.NotNull(c.TipoRetorno);
        Assert.NotNull(c.CuerpoBloque);
        Assert.Empty(Assert.IsType<LambdaSintaxis>(valores[3]).Parametros);
        Assert.IsType<BinariaSintaxis>(valores[4]);
    }

    // ----- Enlace -----

    [Fact]
    public void Un_programa_con_lambdas_correcto_no_tiene_diagnosticos()
    {
        var (programa, diagnosticos) = Enlazar("""
            funcion doble(n: entero): entero { regresar n * 2; }
            funcion aplicar(f: funcion(entero): entero, v: entero): entero { regresar f(v); }
            funcion principal() {
                sea numeros = [3, 1, 2];
                sea pares = numeros.filtrar(n => n % 2 == 0);
                sea textos = numeros.transformar(n => "n" + n);
                sea ordenados = numeros.ordenarPor(n => -n);
                sea total = numeros.reducir(0, (acumulado, n) => acumulado + n);
                sea alguno = numeros.alguno(n => n > 2) && numeros.todos(n => n > 0);
                numeros.paraCada(n => imprimir(n));
                imprimir(aplicar(doble, 4) + aplicar(n => n + 1, 4));
                var f: funcion(entero): entero = doble;
                f = (x: entero) => x;
                sea umbral = 2;
                sea sobre = numeros.filtrar(n => n > umbral);
                sea cuenta: funcion(): entero = () => numeros.cantidad;
                imprimir(cuenta() + f(1) + sobre.cantidad + total + textos[0] + ordenados[0] + pares.cantidad + alguno);
            }
            """);

        Assert.Empty(diagnosticos);
        var principal = programa.Funciones[2];
        Assert.NotNull(principal.Lambdas);
        Assert.Equal(["umbral", "numeros"], principal.Lambdas!.Capturadas.Select(v => v.Nombre).OrderDescending());
        Assert.Equal(11, principal.Lambdas.Lambdas.Length);
        Assert.Null(programa.Funciones[1].Lambdas);
    }

    [Fact]
    public void Los_tipos_de_una_lambda_salen_del_contexto_o_se_escriben()
    {
        var (programa, diagnosticos) = Enlazar("""
            funcion principal() {
                sea a = (x: entero, y: texto) => y + x;
                sea b: funcion(decimal): decimal = x => x * 2;
                sea c = (): texto => { regresar "c"; };
            }
            """);

        Assert.Empty(diagnosticos);
        var cuerpo = programa.Funciones[0].Cuerpo.Sentencias.Cast<DeclaracionVariableEnlazada>().ToList();
        Assert.Equal("funcion(entero, texto): texto", cuerpo[0].Variable.Tipo.Nombre);
        Assert.Equal("funcion(decimal): decimal", cuerpo[1].Variable.Tipo.Nombre);
        Assert.Equal("funcion(): texto", cuerpo[2].Variable.Tipo.Nombre);
        Assert.Same(cuerpo[1].Variable.Tipo, TipoLince.Funcion([TipoLince.Decimal], TipoLince.Decimal));
    }

    [Fact]
    public void Una_lambda_sin_tipos_y_sin_contexto_es_LN3050()
    {
        var error = UnicoError("sea f = x => x * 2;");

        Assert.Equal(Codigos.LambdaSinTipos, error.Codigo);
        Assert.Contains("(x: entero) =>", error.Mensaje);
    }

    [Fact]
    public void Una_lambda_de_bloque_sin_tipo_de_retorno_es_LN3051()
    {
        Assert.Equal(Codigos.LambdaSinRetorno, UnicoError("sea f = (x: entero) => { regresar x; };").Codigo);
    }

    [Theory]
    [InlineData("sea n = 5; n(1);", "'entero' y no se puede llamar")]
    [InlineData("sea f: funcion(entero)? = nulo; f(1);", "puede ser nulo")]
    [InlineData("sea f = (x: entero) => x; f(1)(2);", "'entero' y no se puede llamar")]
    public void Llamar_a_lo_que_no_es_funcion_es_error(string cuerpo, string mensaje)
    {
        Assert.Contains(mensaje, UnicoError(cuerpo).Mensaje);
    }

    [Theory]
    [InlineData("sea l = [1]; l.filtrar((a, b) => verdadero);", "espera una función de 1 parámetro (entero) y la lambda tiene 2")]
    [InlineData("sea l = [1]; l.filtrar((x: texto) => verdadero);", "es 'texto' y aquí se espera 'entero'")]
    [InlineData("sea l = [1]; l.filtrar(x => x + 1);", "debe regresar 'logico' y esta expresión es 'entero'")]
    [InlineData("sea l = [1]; l.transformar(x => imprimir(x));", "debe regresar un valor")]
    [InlineData("sea l = [[1]]; l.ordenarPor(x => x);", "debe ser entero, decimal o texto")]
    [InlineData("sea l = [1]; l.reducir(nulo, (a, b) => a);", "tipo concreto")]
    [InlineData("sea f: funcion(entero): entero = x => x; f(\"a\");", "debe ser 'entero' y este es 'texto'")]
    [InlineData("sea f: funcion(entero): entero = x => x; f();", "espera 1 argumento y recibió 0")]
    public void Las_lambdas_y_sus_llamadas_se_comprueban(string cuerpo, string mensaje)
    {
        Assert.Contains(mensaje, UnicoError(cuerpo).Mensaje);
    }

    [Fact]
    public void Una_lambda_en_el_valor_inicial_de_un_campo_es_LN3055()
    {
        Assert.Equal(Codigos.LambdaEnCampo, UnicoError("", "clase A { f: funcion(): entero = () => 1; }").Codigo);
    }

    [Fact]
    public void Romper_dentro_de_una_lambda_no_sale_del_ciclo_de_afuera()
    {
        Assert.Equal(Codigos.FueraDeCiclo, UnicoError("mientras verdadero { sea f = (): entero => { romper; regresar 1; }; }").Codigo);
    }

    [Fact]
    public void Lo_sabido_sobre_nulos_no_entra_en_la_lambda()
    {
        Assert.Equal(Codigos.MiembroDeNulable, UnicoError("sea t: texto? = \"a\"; si t != nulo { sea f = (): entero => { regresar t.longitud; }; }").Codigo);
    }

    // ----- Extremo a extremo -----

    [Fact]
    public void Orden_superior_sobre_listas()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                sea numeros = [5, 3, 8, 1, 9, 2];
                imprimir(numeros.filtrar(n => n % 2 == 1).transformar(n => "" + n * 10).unir(" "));
                imprimir(numeros.ordenarPor(n => n).invertir().transformar(n => "" + n).unir(","));
                imprimir(numeros.ordenar()[0] + numeros.maximo() + numeros.minimo() + numeros.suma());
                imprimir(numeros.promedio());
                imprimir(numeros.reducir(1, (p, n) => p * n));
                imprimir(numeros.reducir("", (t, n) => t + n));
                imprimir(numeros.alguno(n => n > 8));
                imprimir(numeros.todos(n => n > 0));
                sea nombres = ["eva", "ana", "luis"];
                imprimir(nombres.ordenarPor(n => n.longitud).unir(" "));
                imprimir(nombres.ordenar().unir(" "));
                sea largo = nombres.buscar(n => n.longitud > 3);
                si largo != nulo { imprimir(largo.mayusculas()); }
                imprimir(nombres.buscar(n => n == "x") == nulo);
                var suma = 0;
                numeros.paraCada(n => sumar(n));
            }
            """.Replace("numeros.paraCada(n => sumar(n));", "numeros.paraCada(n => imprimir(n * 0));"));

        Assert.Equal(Lineas("50 30 10 90", "9,8,5,3,2,1", "39", "4.666666666666667", "2160", "538192", "verdadero", "verdadero",
            "eva ana luis", "ana eva luis", "LUIS", "verdadero", "0", "0", "0", "0", "0", "0"), resultado.Salida);
    }

    [Fact]
    public void Una_lambda_del_metodo_de_otro_tipo_y_la_unir_de_textos()
    {
        var resultado = Ejecutar("""
            funcion unirTextos(l: lista<texto>): texto { regresar l.unir("-"); }
            funcion principal() {
                imprimir(unirTextos(["a", "b", "c"]));
                imprimir(["x"].unir("-"));
            }
            """);

        Assert.Equal(Lineas("a-b-c", "x"), resultado.Salida);
    }

    [Fact]
    public void Las_clausuras_ven_y_modifican_las_variables_del_cuerpo()
    {
        var resultado = Ejecutar("""
            funcion contador(inicio: entero): funcion(): entero {
                var actual = inicio;
                regresar () => {
                    actual += 1;
                    regresar actual;
                };
            }

            funcion principal() {
                var total = 0;
                sea sumar = (n: entero) => { total += n; };
                sumar(5);
                sumar(7);
                imprimir(total);

                sea a = contador(10);
                sea b = contador(100);
                imprimir(a() + " " + a() + " " + b());

                sea factor = 3;
                imprimir([1, 2, 3].transformar(n => "" + n * factor).unir(" "));

                var acumulado = "";
                para i en 0..3 {
                    sea letra = "" + i;
                    sea agregar = () => { acumulado = acumulado + letra; };
                    agregar();
                }
                imprimir(acumulado);
            }
            """);

        Assert.Equal(Lineas("12", "11 12 101", "3 6 9", "012"), resultado.Salida);
    }

    [Fact]
    public void Las_lambdas_dentro_de_metodos_capturan_este_y_ven_lo_privado()
    {
        var resultado = Ejecutar("""
            clase Carrito {
                privado precios: lista<decimal>;
                descuento: decimal = 0;

                funcion nuevo() { precios = []; }

                funcion agregar(precio: decimal) { precios.agregar(precio); }

                privado funcion conDescuento(precio: decimal): decimal {
                    regresar precio * (1 - descuento);
                }

                funcion total(): decimal {
                    regresar precios.transformar(p => conDescuento(p)).suma();
                }

                funcion caros(minimo: decimal): entero {
                    regresar precios.filtrar(p => este.conDescuento(p) >= minimo).cantidad;
                }
            }

            estructura Punto {
                x: entero;
                funcion desplazados(cuantos: entero): lista<entero> {
                    sea salida: lista<entero> = [];
                    para i en 0..cuantos { salida.agregar(x + i); }
                    regresar salida.transformar(v => v + x);
                }
            }

            funcion principal() {
                sea carrito = nuevo Carrito();
                carrito.agregar(100);
                carrito.agregar(50);
                carrito.descuento = 0.1;
                imprimir(carrito.total());
                imprimir(carrito.caros(60));
                sea p = nuevo Punto(10);
                imprimir(p.desplazados(3).transformar(v => "" + v).unir(" "));
            }
            """);

        Assert.Equal(Lineas("135", "1", "20 21 22"), resultado.Salida);
    }

    [Fact]
    public void Funciones_como_valores_variables_campos_y_lambdas_anidadas()
    {
        var resultado = Ejecutar("""
            clase Boton {
                etiqueta: texto;
                alPulsar: funcion(texto);

                funcion pulsar() { alPulsar(etiqueta); }
            }

            funcion doble(n: entero): entero { regresar n * 2; }
            funcion componer(f: funcion(entero): entero, g: funcion(entero): entero): funcion(entero): entero {
                regresar x => g(f(x));
            }

            funcion principal() {
                var operacion: funcion(entero, entero): entero = (a, b) => a + b;
                imprimir(operacion(2, 3));
                operacion = (a, b) => a * b;
                imprimir(operacion(2, 3));

                sea boton = nuevo Boton("aceptar", t => imprimir("pulsado " + t));
                boton.pulsar();
                boton.alPulsar = t => imprimir(t.mayusculas());
                boton.pulsar();

                sea cuadruple = componer(doble, doble);
                imprimir(cuadruple(5));

                sea sumador = (a: entero) => (b: entero) => a + b;
                imprimir(sumador(10)(5));

                sea operaciones = {"suma": operacion, "doble": (a: entero, b: entero) => doble(a)};
                imprimir(operaciones["doble"](7, 0));
            }
            """);

        Assert.Equal(Lineas("5", "6", "pulsado aceptar", "ACEPTAR", "20", "15", "14"), resultado.Salida);
    }
}
