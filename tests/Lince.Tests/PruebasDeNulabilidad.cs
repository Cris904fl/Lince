using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Enlace;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Tipos nulables (<c>T?</c>) y el análisis de flujo que permite usarlos sin '?' tras comprobarlos.</summary>
public class PruebasDeNulabilidad : IDisposable
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

    private const string Nodo = """
        clase Nodo {
            valor: entero;
            siguiente: Nodo?;
            funcion nuevo(valor: entero) { este.valor = valor; siguiente = nulo; }
            funcion describir(): texto { regresar "" + valor; }
        }
        funcion buscar(n: entero): Nodo? { si n > 0 { regresar nuevo Nodo(n); } regresar nulo; }
        """;

    private static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(string codigo) =>
        Compilador.Enlazar(ArchivoFuente.DesdeTexto(codigo, "prueba.ln"));

    private static ImmutableArray<Diagnostico> Diagnosticos(string cuerpoDePrincipal) =>
        Enlazar($"{Nodo}\nfuncion principal() {{\n{cuerpoDePrincipal}\n}}").Diagnosticos;

    private static Diagnostico UnicoError(string cuerpoDePrincipal) => Assert.Single(Diagnosticos(cuerpoDePrincipal));

    private ResultadoEjecucion Ejecutar(string codigo, [System.Runtime.CompilerServices.CallerMemberName] string nombre = "")
    {
        var resultado = Compilador.Compilar(ArchivoFuente.DesdeTexto(codigo, nombre + ".ln"), _carpeta, nombre);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        return Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!);
    }

    private static string Lineas(params string[] lineas) => string.Concat(lineas.Select(l => l + Environment.NewLine));

    // ----- Tipos -----

    [Fact]
    public void Un_tipo_nulable_se_declara_con_interrogacion_y_es_unico_por_base()
    {
        var (programa, diagnosticos) = Enlazar("funcion f(a: texto?, b: texto?): texto? { regresar a; }\nfuncion principal() { }");

        Assert.Empty(diagnosticos);
        var parametros = programa.Funciones[0].Simbolo.Parametros;
        Assert.Equal("texto?", parametros[0].Tipo.Nombre);
        Assert.Same(parametros[0].Tipo, parametros[1].Tipo);
        Assert.Same(TipoLince.Nulable(TipoLince.Texto), parametros[0].Tipo);
    }

    [Theory]
    [InlineData("sea t: texto = nulo;", "declárelo como 'texto?'")]
    [InlineData("sea n: Nodo = nulo;", "declárelo como 'Nodo?'")]
    [InlineData("sea e: entero = nulo;", "'entero'")]
    public void Nulo_solo_cabe_en_un_tipo_con_interrogacion(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.NuloEnTipoNoNulable, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Theory]
    [InlineData("entero?")]
    [InlineData("logico?")]
    public void Un_tipo_por_valor_no_admite_interrogacion_LN3044(string tipo)
    {
        var (_, diagnosticos) = Enlazar($"funcion principal() {{ sea x: {tipo} = nulo; }}");

        Assert.Equal(Codigos.NulableDeValor, Assert.Single(diagnosticos).Codigo);
    }

    [Fact]
    public void Un_valor_no_nulable_cabe_en_un_sitio_nulable_pero_no_al_reves()
    {
        Assert.Empty(Diagnosticos("sea a: texto? = \"x\"; sea n: Nodo? = nuevo Nodo(1); sea b: texto? = a;"));

        // Declarada como texto? pero recién asignada con un texto: el flujo sabe que no es nulo y se puede usar.
        Assert.Empty(Diagnosticos("sea a: texto? = \"x\"; sea b: texto = a; si a == nulo { }"));

        var error = UnicoError("}\nfuncion f(a: texto?) { sea b: texto = a;");
        Assert.Equal(Codigos.PuedeSerNulo, error.Codigo);
        Assert.Contains("puede ser nulo", error.Mensaje);
    }

    [Theory]
    [InlineData("sea n = buscar(1); imprimir(n.valor);")]
    [InlineData("sea n = buscar(1); imprimir(n.describir());")]
    [InlineData("sea n = buscar(1); n.valor = 2;")]
    [InlineData("sea n = nuevo Nodo(1); imprimir(n.siguiente.valor);")]
    public void Usar_un_miembro_de_algo_que_puede_ser_nulo_es_LN3042(string cuerpo)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.MiembroDeNulable, error.Codigo);
        Assert.Contains("!= nulo", error.Mensaje);
    }

    [Theory]
    [InlineData("sea n = nuevo Nodo(1); si n == nulo { }", "'Nodo?'")]
    [InlineData("sea t = \"x\"; si t != nulo { }", "'texto?'")]
    [InlineData("si 1 == nulo { }", "'entero'")]
    public void Comparar_con_nulo_algo_que_nunca_lo_es_es_LN3043(string cuerpo, string tipo)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.NuncaEsNulo, error.Codigo);
        Assert.Contains(tipo, error.Mensaje);
    }

    [Fact]
    public void Pasar_o_regresar_un_nulable_donde_se_espera_el_tipo_sin_interrogacion_es_LN3041()
    {
        Assert.Equal(Codigos.PuedeSerNulo, UnicoError("sea n = buscar(1); usar(n);\n}\nfuncion usar(n: Nodo) {").Codigo);
        Assert.Equal(Codigos.PuedeSerNulo, UnicoError("}\nfuncion f(): Nodo { regresar buscar(1);").Codigo);
    }

    // ----- Flujo -----

    [Theory]
    [InlineData("sea n = buscar(1); si n != nulo { imprimir(n.valor); }")]
    [InlineData("sea n = buscar(1); si n == nulo { imprimir(\"nada\"); } sino { imprimir(n.valor); }")]
    [InlineData("sea n = buscar(1); si n == nulo { regresar; } imprimir(n.valor);")]
    [InlineData("sea n = buscar(1); si !(n == nulo) { imprimir(n.valor); }")]
    [InlineData("sea n = buscar(1); si n != nulo && n.valor > 0 { imprimir(n.describir()); }")]
    [InlineData("sea n = buscar(1); si n == nulo || n.valor == 0 { imprimir(\"vacío o cero\"); }")]
    [InlineData("sea n = buscar(1); sea m = buscar(2); si n != nulo && m != nulo { imprimir(n.valor + m.valor); }")]
    [InlineData("sea n = buscar(1); mientras n != nulo { imprimir(n.valor); romper; }")]
    [InlineData("sea n = nuevo Nodo(1); si n.siguiente != nulo { imprimir(n.siguiente.valor); }")]
    [InlineData("var n: Nodo? = nulo; n = nuevo Nodo(1); imprimir(n.valor);")]
    [InlineData("sea n = buscar(1); si n != nulo { sea m: Nodo = n; imprimir(m.valor); }")]
    [InlineData("sea n = buscar(1); si n != nulo { { imprimir(n.valor); } }")]
    public void Tras_comprobar_que_no_es_nulo_se_usa_como_si_no_tuviera_interrogacion(string cuerpo)
    {
        Assert.Empty(Diagnosticos(cuerpo));
    }

    [Theory]
    [InlineData("sea n = buscar(1); si n != nulo { } imprimir(n.valor);")]
    [InlineData("sea n = buscar(1); si n == nulo { imprimir(\"nada\"); } imprimir(n.valor);")]
    [InlineData("sea n = buscar(1); si n != nulo || n.valor > 0 { }")]
    [InlineData("sea n = buscar(1); si n == nulo && n.valor > 0 { }")]
    [InlineData("var n = buscar(1); si n != nulo { n = buscar(2); imprimir(n.valor); }")]
    [InlineData("var n = buscar(1); si n != nulo { mientras verdadero { imprimir(n.valor); n = buscar(2); } }")]
    [InlineData("var n = nuevo Nodo(1); si n.siguiente != nulo { n.siguiente = nulo; imprimir(n.siguiente.valor); }")]
    [InlineData("var n = nuevo Nodo(1); si n.siguiente != nulo { n = nuevo Nodo(2); imprimir(n.siguiente.valor); }")]
    [InlineData("sea n = buscar(1); si n != nulo { } sino { } imprimir(n.valor);")]
    public void Donde_el_flujo_no_garantiza_que_no_es_nulo_sigue_siendo_error(string cuerpo)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.MiembroDeNulable, error.Codigo);
    }

    [Fact]
    public void Una_asignacion_dentro_de_un_ciclo_invalida_lo_sabido_antes_del_ciclo()
    {
        var (programa, diagnosticos) = Enlazar(Nodo + """

            funcion principal() {
                var n = buscar(1);
                si n == nulo { regresar; }
                imprimir(n.valor);
                mientras n != nulo {
                    n = n.siguiente;
                }
            }
            """);

        Assert.Empty(diagnosticos);
        // 'n' se usa sin '?' antes del ciclo (viene convertido) y la condición del ciclo lo lee como 'Nodo?'.
        var cuerpo = programa.Funciones[1].Cuerpo.Sentencias;
        var imprimir = (ImprimirEnlazado)((ExpresionSentenciaEnlazada)cuerpo[2]).Expresion;
        var campo = (CampoEnlazado)((ConversionEnlazada)imprimir.Valor!).Expresion;
        Assert.IsType<ConversionEnlazada>(campo.Objeto);
        var mientras = (MientrasEnlazado)cuerpo[3];
        Assert.IsType<VariableEnlazada>(((BinariaEnlazada)mientras.Condicion).Izquierda);
    }

    // ----- Extremo a extremo -----

    [Fact]
    public void Un_texto_nulable_se_imprime_vacio_y_se_concatena_como_vacio()
    {
        var resultado = Ejecutar("""
            funcion saludo(nombre: texto?): texto {
                si nombre == nulo {
                    regresar "Hola, desconocido";
                }
                regresar "Hola, " + nombre;
            }

            funcion principal() {
                sea t: texto? = nulo;
                imprimir(t);
                imprimir("[" + t + "]");
                imprimir(t == nulo);
                imprimir(saludo(nulo));
                imprimir(saludo("Ana"));
            }
            """);

        Assert.Equal(Lineas("", "[]", "verdadero", "Hola, desconocido", "Hola, Ana"), resultado.Salida);
    }

    [Fact]
    public void Un_arbol_binario_con_hijos_nulables()
    {
        var resultado = Ejecutar("""
            clase Arbol {
                valor: entero;
                izquierda: Arbol?;
                derecha: Arbol?;

                funcion nuevo(valor: entero) {
                    este.valor = valor;
                    izquierda = nulo;
                    derecha = nulo;
                }

                funcion insertar(nuevoValor: entero) {
                    si nuevoValor < valor {
                        si izquierda == nulo {
                            izquierda = nuevo Arbol(nuevoValor);
                        } sino {
                            izquierda.insertar(nuevoValor);
                        }
                    } sino {
                        si derecha == nulo {
                            derecha = nuevo Arbol(nuevoValor);
                        } sino {
                            derecha.insertar(nuevoValor);
                        }
                    }
                }

                funcion enOrden(): texto {
                    var resultado = "";
                    si izquierda != nulo {
                        resultado = izquierda.enOrden() + " ";
                    }
                    resultado = resultado + valor;
                    si derecha != nulo {
                        resultado = resultado + " " + derecha.enOrden();
                    }
                    regresar resultado;
                }

                funcion profundidad(): entero {
                    var i = 0;
                    var d = 0;
                    si izquierda != nulo { i = izquierda.profundidad(); }
                    si derecha != nulo { d = derecha.profundidad(); }
                    si i > d { regresar i + 1; }
                    regresar d + 1;
                }
            }

            funcion principal() {
                sea raiz = nuevo Arbol(50);
                raiz.insertar(30);
                raiz.insertar(70);
                raiz.insertar(20);
                raiz.insertar(40);
                raiz.insertar(60);
                raiz.insertar(80);
                raiz.insertar(10);
                imprimir(raiz.enOrden());
                imprimir(raiz.profundidad());
            }
            """);

        Assert.Equal(Lineas("10 20 30 40 50 60 70 80", "4"), resultado.Salida);
    }
}
