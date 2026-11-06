using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Enlace;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Fase 2: clases, estructuras, campos, métodos, nuevo, este y privado, en las tres capas.</summary>
public class PruebasDeObjetos : IDisposable
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

    private const string Cuenta = """
        clase Cuenta {
            titular: texto;
            privado saldo: decimal = 0;

            funcion depositar(monto: decimal) { saldo += monto; }
            funcion verSaldo(): decimal { regresar saldo; }
            privado funcion secreto(): entero { regresar 42; }
        }
        """;

    private static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(string codigo) =>
        Compilador.Enlazar(ArchivoFuente.DesdeTexto(codigo, "prueba.ln"));

    private static Diagnostico UnicoError(string cuerpoDePrincipal, string declaraciones = Cuenta)
    {
        var (_, diagnosticos) = Enlazar($"{declaraciones}\nfuncion principal() {{\n{cuerpoDePrincipal}\n}}");
        return Assert.Single(diagnosticos);
    }

    private ResultadoEjecucion Ejecutar(string codigo, [System.Runtime.CompilerServices.CallerMemberName] string nombre = "")
    {
        var resultado = Compilador.Compilar(ArchivoFuente.DesdeTexto(codigo, nombre + ".ln"), _carpeta, nombre);
        Assert.True(resultado.Exito, string.Join(Environment.NewLine, resultado.Diagnosticos));
        return Ejecutor.EjecutarCapturando(resultado.RutaEnsamblado!);
    }

    private static string Lineas(params string[] lineas) => string.Concat(lineas.Select(l => l + Environment.NewLine));

    // ----- Sintaxis -----

    [Fact]
    public void Analiza_una_clase_con_campos_metodos_y_constructor()
    {
        var arbol = ArbolSintaxis.Analizar("""
            clase Cuenta {
                titular: texto;
                privado saldo: decimal = 0;
                funcion nuevo(t: texto) { este.titular = t; }
                publico funcion depositar(m: decimal) { saldo += m; }
            }
            """);

        Assert.Empty(arbol.Diagnosticos);
        var tipo = Assert.Single(arbol.Raiz.Tipos);
        Assert.False(tipo.EsEstructura);
        Assert.Equal("Cuenta", tipo.Nombre.Texto);
        Assert.Equal(["titular", "saldo"], tipo.Campos.Select(c => c.Nombre.Texto));
        Assert.True(tipo.Campos[1].EsPrivado);
        Assert.NotNull(tipo.Campos[1].Inicial);
        Assert.Equal(2, tipo.Metodos.Length);
        Assert.True(tipo.Metodos[0].EsConstructor);
        Assert.False(tipo.Metodos[1].EsPrivada);
    }

    [Fact]
    public void Analiza_nuevo_este_miembros_y_asignacion_a_campo()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() { sea c = nuevo Cuenta(\"a\", 1); c.saldo = este.x.y; c.depositar(1).z = 2; }");

        Assert.Empty(arbol.Diagnosticos);
        var sentencias = arbol.Raiz.Funciones[0].Cuerpo.Sentencias;
        var nuevo = Assert.IsType<NuevoSintaxis>(Assert.IsType<DeclaracionVariableSintaxis>(sentencias[0]).Valor);
        Assert.Equal("Cuenta", nuevo.Tipo.Texto);
        Assert.Equal(2, nuevo.Argumentos.Length);

        var asignacion = Assert.IsType<AsignacionSintaxis>(sentencias[1]);
        Assert.Equal("saldo", Assert.IsType<MiembroSintaxis>(asignacion.Objetivo).Nombre.Texto);
        var cadena = Assert.IsType<MiembroSintaxis>(asignacion.Valor);
        Assert.IsType<EsteSintaxis>(Assert.IsType<MiembroSintaxis>(cadena.Objeto).Objeto);

        var sobreLlamada = Assert.IsType<AsignacionSintaxis>(sentencias[2]);
        Assert.IsType<LlamadaSintaxis>(Assert.IsType<MiembroSintaxis>(sobreLlamada.Objetivo).Objeto);
    }

    [Fact]
    public void Asignar_a_algo_que_no_es_variable_ni_campo_es_LN2006()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() { f() = 1; }");

        Assert.Equal(Codigos.ObjetivoDeAsignacionInvalido, Assert.Single(arbol.Diagnosticos).Codigo);
    }

    [Fact]
    public void Dentro_de_una_clase_solo_van_campos_y_funciones()
    {
        var arbol = ArbolSintaxis.Analizar("clase C { sea x = 1; a: entero; }\nfuncion principal() { }");

        var error = Assert.Single(arbol.Diagnosticos);
        Assert.Equal(Codigos.SeEsperabaMiembro, error.Codigo);
        Assert.Single(arbol.Raiz.Tipos[0].Campos);
    }

    // ----- Enlace -----

    [Fact]
    public void Un_programa_con_objetos_correcto_no_tiene_diagnosticos()
    {
        var (programa, diagnosticos) = Enlazar(Cuenta + """

            estructura Punto { x: entero; y: entero; funcion suma(): entero { regresar x + y; } }
            funcion principal() {
                sea c = nuevo Cuenta("Ana");
                c.depositar(10);
                c.titular = "Eva";
                var p = nuevo Punto(1, 2);
                p.x = 5;
                imprimir(c.titular + c.verSaldo() + p.suma());
                si c == nulo || c != c { }
            }
            """);

        Assert.Empty(diagnosticos);
        Assert.Equal(2, programa.Tipos.Length);
        Assert.Equal(["titular", "saldo"], programa.Tipos[0].Tipo.Campos.Select(c => c.Nombre));
        Assert.Single(programa.Tipos[0].Iniciales);
        Assert.Null(programa.Tipos[0].Constructor);
        Assert.Equal(["titular"], programa.Tipos[0].Tipo.ParametrosDeNuevo.Select(p => p.Nombre));
    }

    [Fact]
    public void El_constructor_explicito_define_los_parametros_de_nuevo()
    {
        var (programa, diagnosticos) = Enlazar("""
            clase C { a: entero; b: entero; funcion nuevo(valor: entero) { a = valor; b = valor * 2; } }
            funcion principal() { sea c = nuevo C(1); }
            """);

        Assert.Empty(diagnosticos);
        Assert.NotNull(programa.Tipos[0].Constructor);
        Assert.Equal(["valor"], programa.Tipos[0].Tipo.ParametrosDeNuevo.Select(p => p.Nombre));
    }

    [Theory]
    [InlineData("sea c: Cuenta = nulo;")]
    [InlineData("sea c = nuevo Cuenta(\"a\"); sea d: Cuenta = c; si d == c { }")]
    public void Una_clase_admite_nulo_y_se_compara_por_identidad(string cuerpo)
    {
        var (_, diagnosticos) = Enlazar($"{Cuenta}\nfuncion principal() {{ {cuerpo} }}");

        Assert.Empty(diagnosticos);
    }

    [Fact]
    public void Tipo_inexistente_es_LN3031()
    {
        var error = UnicoError("sea x = nuevo Cosa();");

        Assert.Equal(Codigos.TipoNoExiste, error.Codigo);
    }

    [Fact]
    public void Tipo_duplicado_es_LN3032()
    {
        Assert.Equal(Codigos.TipoDuplicado, UnicoError("", "clase A { }\nestructura A { }").Codigo);
    }

    [Theory]
    [InlineData("clase A { x: entero; x: texto; }")]
    [InlineData("clase A { x: entero; funcion x() { } }")]
    [InlineData("clase A { funcion nuevo() { } funcion nuevo() { } }")]
    public void Miembro_duplicado_es_LN3033(string declaracion)
    {
        Assert.Equal(Codigos.MiembroDuplicado, UnicoError("", declaracion).Codigo);
    }

    [Theory]
    [InlineData("sea c = nuevo Cuenta(\"a\"); imprimir(c.nombre);", "no tiene un campo llamado 'nombre'")]
    [InlineData("sea c = nuevo Cuenta(\"a\"); c.cerrar();", "no tiene un método llamado 'cerrar'")]
    [InlineData("sea t = \"x\"; imprimir(t.longitud);", "no tiene campos")]
    public void Miembro_inexistente_es_LN3034(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.MiembroNoExiste, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Theory]
    [InlineData("sea c = nuevo Cuenta(\"a\"); imprimir(c.saldo);")]
    [InlineData("sea c = nuevo Cuenta(\"a\"); c.saldo = 1;")]
    [InlineData("sea c = nuevo Cuenta(\"a\"); imprimir(c.secreto());")]
    public void Usar_un_miembro_privado_desde_fuera_es_LN3035(string cuerpo)
    {
        Assert.Equal(Codigos.MiembroPrivado, UnicoError(cuerpo).Codigo);
    }

    [Fact]
    public void Este_fuera_de_un_metodo_es_LN3036()
    {
        Assert.Equal(Codigos.EsteFueraDeMetodo, UnicoError("imprimir(este.titular);").Codigo);
    }

    [Fact]
    public void Privado_en_una_funcion_libre_es_LN3037()
    {
        Assert.Equal(Codigos.ModificadorFueraDeTipo, UnicoError("", "privado funcion f() { }").Codigo);
    }

    [Theory]
    [InlineData("clase A { funcion nuevo(): entero { regresar 1; } }")]
    [InlineData("funcion nuevo() { }")]
    public void Constructor_mal_formado_es_LN3038(string declaracion)
    {
        Assert.Equal(Codigos.ConstructorMalFormado, UnicoError("", declaracion).Codigo);
    }

    [Theory]
    [InlineData("sea p = nuevo Punto(1, 2); p.x = 3;", "se declaró con 'sea'")]
    [InlineData("imprimir(hacer().x); hacer().x = 3;", "no está guardada en una variable")]
    public void Cambiar_un_campo_de_una_estructura_en_sitio_inmutable_es_LN3039(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo, "estructura Punto { x: entero; y: entero; }\nfuncion hacer(): Punto { regresar nuevo Punto(1, 2); }");

        Assert.Equal(Codigos.CampoDeValorInmutable, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Fact]
    public void Cambiar_un_campo_de_una_clase_en_variable_sea_es_valido()
    {
        var (_, diagnosticos) = Enlazar($"{Cuenta}\nfuncion principal() {{ sea c = nuevo Cuenta(\"a\"); c.titular = \"b\"; }}");

        Assert.Empty(diagnosticos);
    }

    [Fact]
    public void Nuevo_de_un_tipo_basico_es_LN3040()
    {
        Assert.Equal(Codigos.NuevoDeTipoBasico, UnicoError("sea x = nuevo entero();").Codigo);
    }

    [Theory]
    [InlineData("sea c = nuevo Cuenta();", "espera 1 argumento y recibió 0")]
    [InlineData("sea c = nuevo Cuenta(1);", "debe ser 'texto' y este es 'entero'")]
    [InlineData("sea c = nuevo Cuenta(\"a\"); c.depositar(\"x\");", "debe ser 'decimal' y este es 'texto'")]
    public void Los_argumentos_de_nuevo_y_de_los_metodos_se_comprueban(string cuerpo, string mensaje)
    {
        Assert.Contains(mensaje, UnicoError(cuerpo).Mensaje);
    }

    [Theory]
    [InlineData("sea c = nuevo Cuenta(\"a\"); sea e: entero = c;")]
    [InlineData("sea p: Punto = nulo;")]
    [InlineData("sea p = nuevo Punto(1, 2); si p == p { }")]
    [InlineData("sea c = nuevo Cuenta(\"a\"); imprimir(c);")]
    public void Los_tipos_de_usuario_no_se_mezclan_con_los_basicos(string cuerpo)
    {
        var (_, diagnosticos) = Enlazar($"{Cuenta}\nestructura Punto {{ x: entero; y: entero; }}\nfuncion principal() {{ {cuerpo} }}");

        Assert.Single(diagnosticos);
    }

    [Fact]
    public void Un_campo_se_nombra_a_secas_dentro_del_metodo_y_una_local_lo_tapa()
    {
        var (programa, diagnosticos) = Enlazar("""
            clase A {
                x: entero;
                funcion f(): entero { sea x = 10; regresar x + este.x; }
            }
            funcion principal() { }
            """);

        Assert.Empty(diagnosticos);
        var regresar = (RegresarEnlazado)programa.Tipos[0].Metodos[0].Cuerpo.Sentencias[1];
        var suma = Assert.IsType<BinariaEnlazada>(regresar.Valor);
        Assert.IsType<VariableEnlazada>(suma.Izquierda);
        Assert.IsType<CampoEnlazado>(suma.Derecha);
    }

    // ----- Extremo a extremo -----

    [Fact]
    public void Una_clase_con_constructor_implicito_campos_iniciales_y_metodos()
    {
        var resultado = Ejecutar("""
            clase Contador {
                nombre: texto;
                valor: entero = 0;
                paso: entero;

                funcion avanzar() { valor += paso; }
                funcion describir(): texto { regresar nombre + " = " + valor; }
            }

            funcion principal() {
                sea c = nuevo Contador("c", 5);
                c.avanzar();
                c.avanzar();
                imprimir(c.describir());
                c.valor = 100;
                imprimir(c.valor);
            }
            """);

        Assert.Equal(Lineas("c = 10", "100"), resultado.Salida);
    }

    [Fact]
    public void Un_constructor_explicito_corre_despues_de_los_valores_iniciales()
    {
        var resultado = Ejecutar("""
            clase Rango {
                desde: entero = -1;
                hasta: entero = -1;
                ancho: entero;

                funcion nuevo(desde: entero, hasta: entero) {
                    imprimir("inicial: " + este.desde + ".." + este.hasta);
                    este.desde = desde;
                    este.hasta = hasta;
                    ancho = hasta - desde;
                }
            }

            funcion principal() {
                sea r = nuevo Rango(3, 10);
                imprimir(r.desde + ".." + r.hasta + " ancho " + r.ancho);
            }
            """);

        Assert.Equal(Lineas("inicial: -1..-1", "3..10 ancho 7"), resultado.Salida);
    }

    [Fact]
    public void Las_clases_son_referencias_y_las_estructuras_valores()
    {
        var resultado = Ejecutar("""
            clase Caja { valor: entero; }
            estructura Par { a: entero; b: entero; funcion sumar(): entero { regresar a + b; } }

            funcion principal() {
                sea caja1 = nuevo Caja(1);
                sea caja2 = caja1;
                caja2.valor = 2;
                imprimir(caja1.valor);
                imprimir(caja1 == caja2);
                imprimir(caja1 == nuevo Caja(2));

                var par1 = nuevo Par(1, 2);
                var par2 = par1;
                par2.a = 10;
                imprimir(par1.a + " " + par2.a + " " + par2.sumar());
            }
            """);

        Assert.Equal(Lineas("2", "verdadero", "falso", "1 10 12"), resultado.Salida);
    }

    [Fact]
    public void Un_metodo_de_estructura_modifica_el_original_cuando_esta_en_una_variable()
    {
        var resultado = Ejecutar("""
            estructura Punto {
                x: entero;
                y: entero;
                funcion mover(dx: entero) { x += dx; }
            }
            clase Figura {
                centro: Punto;
                funcion desplazar() { centro.mover(100); }
            }

            funcion principal() {
                var p = nuevo Punto(1, 1);
                p.mover(5);
                imprimir(p.x);

                sea f = nuevo Figura(nuevo Punto(0, 0));
                f.centro.mover(1);
                f.desplazar();
                f.centro.x += 1;
                imprimir(f.centro.x);
            }
            """);

        Assert.Equal(Lineas("6", "102"), resultado.Salida);
    }

    [Fact]
    public void Los_objetos_se_pasan_y_se_regresan_entre_funciones()
    {
        var resultado = Ejecutar("""
            clase Nodo {
                valor: entero;
                siguiente: Nodo;

                funcion nuevo(valor: entero) {
                    este.valor = valor;
                    siguiente = nulo;
                }
            }

            funcion agregar(cabeza: Nodo, valor: entero): Nodo {
                sea nuevoNodo = nuevo Nodo(valor);
                nuevoNodo.siguiente = cabeza;
                regresar nuevoNodo;
            }

            funcion suma(nodo: Nodo): entero {
                var total = 0;
                var actual = nodo;
                mientras actual != nulo {
                    total += actual.valor;
                    actual = actual.siguiente;
                }
                regresar total;
            }

            funcion principal() {
                var cabeza: Nodo = nulo;
                para i en 1..5 {
                    cabeza = agregar(cabeza, i);
                }
                imprimir(suma(cabeza));
            }
            """);

        Assert.Equal(Lineas("10"), resultado.Salida);
    }

    [Fact]
    public void Un_metodo_llama_a_otro_del_mismo_tipo_y_a_funciones_libres()
    {
        var resultado = Ejecutar("""
            funcion doble(n: entero): entero { regresar n * 2; }

            clase Calculadora {
                privado acumulado: entero = 0;

                privado funcion sumar(n: entero) { acumulado += doble(n); }

                funcion sumarTodos(hasta: entero): entero {
                    para i en 1..hasta + 1 { sumar(i); }
                    regresar acumulado;
                }
            }

            funcion principal() {
                imprimir(nuevo Calculadora().sumarTodos(4));
            }
            """);

        Assert.Equal(Lineas("20"), resultado.Salida);
    }
}
