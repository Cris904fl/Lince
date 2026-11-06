using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Ejecucion;
using Lince.Compiler.Enlace;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Fase 3a: lista&lt;T&gt;, mapa&lt;K, V&gt;, literales, índices, para...en y miembros integrados.</summary>
public class PruebasDeColecciones : IDisposable
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
    public void Analiza_tipos_genericos_literales_indices_y_para_en()
    {
        var arbol = ArbolSintaxis.Analizar("""
            funcion f(a: lista<entero>, b: mapa<texto, lista<Cuenta>>?) {
                sea l = [1, 2, 3];
                sea m = {"a": 1, "b": 2};
                l[0] = m["a"] + l[1];
                para x en l { }
                para i en 0..3 { }
                sea v = nuevo lista<texto>();
            }
            """);

        Assert.Empty(arbol.Diagnosticos);
        var funcion = arbol.Raiz.Funciones[0];
        Assert.Equal("entero", Assert.Single(funcion.Parametros[0].Tipo.Argumentos).Palabra.Texto);
        var mapa = funcion.Parametros[1].Tipo;
        Assert.True(mapa.EsNulable);
        Assert.Equal(2, mapa.Argumentos.Length);
        Assert.Equal("Cuenta", mapa.Argumentos[1].Argumentos[0].Palabra.Texto);

        var sentencias = funcion.Cuerpo.Sentencias;
        Assert.Equal(3, Assert.IsType<ListaLiteralSintaxis>(((DeclaracionVariableSintaxis)sentencias[0]).Valor).Elementos.Length);
        Assert.Equal(2, Assert.IsType<MapaLiteralSintaxis>(((DeclaracionVariableSintaxis)sentencias[1]).Valor).Pares.Length);
        var asignacion = Assert.IsType<AsignacionSintaxis>(sentencias[2]);
        Assert.IsType<IndiceSintaxis>(asignacion.Objetivo);
        Assert.False(Assert.IsType<ParaSintaxis>(sentencias[3]).EsRango);
        Assert.True(Assert.IsType<ParaSintaxis>(sentencias[4]).EsRango);
        Assert.Equal("texto", Assert.IsType<NuevoSintaxis>(((DeclaracionVariableSintaxis)sentencias[5]).Valor).Tipo.Argumentos[0].Palabra.Texto);
    }

    [Fact]
    public void El_indice_se_encadena_con_miembros_y_llamadas()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() { x = a.b[1].c(2)[\"k\"]; }");

        Assert.Empty(arbol.Diagnosticos);
        var valor = ((AsignacionSintaxis)arbol.Raiz.Funciones[0].Cuerpo.Sentencias[0]).Valor;
        var externo = Assert.IsType<IndiceSintaxis>(valor);
        var llamada = Assert.IsType<LlamadaSintaxis>(externo.Objeto);
        var miembro = Assert.IsType<MiembroSintaxis>(llamada.Objetivo);
        Assert.IsType<IndiceSintaxis>(miembro.Objeto);
    }

    // ----- Enlace -----

    [Fact]
    public void Un_programa_con_colecciones_correcto_no_tiene_diagnosticos()
    {
        var (programa, diagnosticos) = Enlazar("""
            clase Cuenta { saldo: decimal; }
            funcion principal() {
                sea enteros = [1, 2, 3];
                sea mixtos = [1, 2.5];
                sea cuentas: lista<Cuenta> = [];
                sea vacio: mapa<texto, entero> = {};
                sea edades = {"ana": 30, "eva": 41};
                cuentas.agregar(nuevo Cuenta(10));
                enteros[0] = enteros[1] * 2;
                edades["luis"] = 25;
                edades["ana"] += 1;
                var suma: decimal = 0;
                para c en cuentas { suma += c.saldo; }
                para nombre en edades { imprimir(nombre + edades[nombre]); }
                imprimir(enteros.cantidad + edades.cantidad + mixtos.cantidad + "abc".longitud);
                imprimir("Hola".mayusculas().contiene("HO"));
                sea partes = "a,b".partir(",");
                imprimir(partes[1] + edades.claves()[0]);
                sea nulable: lista<entero>? = nulo;
            }
            """);

        Assert.Empty(diagnosticos);
        var cuerpo = programa.Funciones[0].Cuerpo.Sentencias.OfType<DeclaracionVariableEnlazada>().ToList();
        Assert.Equal("lista<entero>", cuerpo[0].Variable.Tipo.Nombre);
        Assert.Equal("lista<decimal>", cuerpo[1].Variable.Tipo.Nombre);
        Assert.Equal("lista<Cuenta>", cuerpo[2].Variable.Tipo.Nombre);
        Assert.Equal("mapa<texto, entero>", cuerpo[4].Variable.Tipo.Nombre);
        Assert.Same(cuerpo[0].Variable.Tipo, TipoLince.Lista(TipoLince.Entero));
    }

    [Theory]
    [InlineData("sea l: lista = [];", "lista<T>")]
    [InlineData("sea m: mapa<texto> = {};", "mapa<K, V>")]
    [InlineData("sea e: Caja<texto> = nuevo Caja();", "no lleva argumentos de tipo")]
    public void Argumentos_de_tipo_mal_puestos_es_LN3045(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo, "clase Caja { }");

        Assert.Equal(Codigos.ArgumentosDeTipo, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Theory]
    [InlineData("sea l = [];")]
    [InlineData("sea m = {};")]
    [InlineData("imprimir([].cantidad);")]
    public void Una_coleccion_vacia_sin_tipo_es_LN3046(string cuerpo)
    {
        Assert.Equal(Codigos.ColeccionVaciaSinTipo, UnicoError(cuerpo).Codigo);
    }

    [Fact]
    public void Una_coleccion_vacia_toma_el_tipo_del_contexto()
    {
        Assert.Empty(Diagnosticos("var l: lista<texto> = []; var m: mapa<entero, lista<entero>> = {}; m = {}; l = [];"));

        var (_, diagnosticos) = Enlazar("""
            clase Caja { elementos: lista<texto> = []; }
            funcion vacia(): lista<entero> { regresar []; }
            funcion principal() { }
            """);
        Assert.Empty(diagnosticos);
    }

    [Theory]
    [InlineData("sea l = [1, \"dos\"];", "deben ser 'entero'")]
    [InlineData("sea m = {\"a\": 1, 2: 3};", "claves del mapa deben ser 'texto'")]
    [InlineData("sea m = {\"a\": 1, \"b\": verdadero};", "valores del mapa deben ser 'entero'")]
    [InlineData("sea l: lista<texto> = [1];", "deben ser 'texto'")]
    public void Elementos_de_tipos_distintos_es_LN3016(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.TipoIncompatible, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Theory]
    [InlineData("sea t = \"abc\"; imprimir(t[0]);", "subtexto")]
    [InlineData("sea n = 5; imprimir(n[0]);", "'entero'")]
    public void Indexar_lo_que_no_es_coleccion_es_LN3047(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.NoSePuedeIndexar, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Theory]
    [InlineData("sea l = [1]; imprimir(l[\"a\"]);", "índice de una lista debe ser 'entero'")]
    [InlineData("sea m = {\"a\": 1}; imprimir(m[1]);", "clave del mapa debe ser 'texto'")]
    [InlineData("sea l = [1]; l[0] = \"x\";", "no se puede asignar un valor de tipo 'texto'")]
    public void El_indice_y_el_valor_asignado_se_comprueban(string cuerpo, string mensaje)
    {
        Assert.Contains(mensaje, UnicoError(cuerpo).Mensaje);
    }

    [Theory]
    [InlineData("sea l = [1]; l.cantidad = 3;")]
    [InlineData("sea t = \"a\"; t.longitud = 3;")]
    public void Escribir_una_propiedad_integrada_es_LN3048(string cuerpo)
    {
        Assert.Equal(Codigos.MiembroDeSoloLectura, UnicoError(cuerpo).Codigo);
    }

    [Theory]
    [InlineData("para x en 5 { }")]
    [InlineData("para x en \"abc\" { }")]
    public void Recorrer_lo_que_no_es_coleccion_es_LN3049(string cuerpo)
    {
        Assert.Equal(Codigos.NoSePuedeRecorrer, UnicoError(cuerpo).Codigo);
    }

    [Theory]
    [InlineData("sea l = [1]; l.empujar(2);", "'lista<entero>' no tiene un miembro llamado 'empujar'; tiene: cantidad, agregar")]
    [InlineData("sea t = \"a\"; imprimir(t.tamano);", "'texto' no tiene un miembro llamado 'tamano'")]
    [InlineData("sea l = [1]; imprimir(l.agregar);", "es un método de 'lista<entero>'")]
    [InlineData("sea l = [1]; imprimir(l.cantidad());", "se lee sin paréntesis")]
    [InlineData("sea l = [1]; l.agregar(\"x\");", "debe ser 'entero' y este es 'texto'")]
    [InlineData("sea t = \"a\"; t.subtexto(1);", "espera 2 argumentos y recibió 1")]
    public void Los_miembros_integrados_se_comprueban(string cuerpo, string mensaje)
    {
        Assert.Contains(mensaje, UnicoError(cuerpo).Mensaje);
    }

    [Fact]
    public void Una_coleccion_nulable_no_se_usa_sin_comprobar()
    {
        Assert.Equal(Codigos.MiembroDeNulable, UnicoError("sea l: lista<entero>? = nulo; imprimir(l[0]);").Codigo);
        Assert.Equal(Codigos.MiembroDeNulable, UnicoError("sea l: lista<entero>? = nulo; para x en l { }").Codigo);
        Assert.Empty(Diagnosticos("sea l: lista<entero>? = nulo; si l != nulo { imprimir(l[0] + l.cantidad); }"));
    }

    [Fact]
    public void Nuevo_de_una_coleccion_no_lleva_argumentos()
    {
        var error = UnicoError("sea l = nuevo lista<entero>(1, 2);");

        Assert.Equal(Codigos.CantidadDeArgumentos, error.Codigo);
        Assert.Contains("use un literal", error.Mensaje);
    }

    // ----- Extremo a extremo -----

    [Fact]
    public void Listas_con_literal_indices_metodos_y_para_en()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                sea numeros = [5, 3, 9];
                numeros.agregar(1);
                numeros.insertar(0, 7);
                numeros[1] = numeros[1] * 10;
                imprimir(numeros.cantidad);
                var acumulado = "";
                para n en numeros {
                    acumulado = acumulado + n + " ";
                }
                imprimir(acumulado);
                imprimir(numeros.contiene(9));
                imprimir(numeros.indiceDe(9));
                imprimir(numeros.quitar(9));
                imprimir(numeros.quitar(100));
                numeros.quitarEn(0);
                var suma = 0;
                para i en 0..numeros.cantidad {
                    suma += numeros[i];
                }
                imprimir(suma);
                numeros.limpiar();
                imprimir(numeros.cantidad);
            }
            """);

        Assert.Equal(Lineas("5", "7 50 3 9 1 ", "verdadero", "3", "verdadero", "falso", "54", "0"), resultado.Salida);
    }

    [Fact]
    public void Mapas_con_literal_indices_metodos_y_para_en()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                sea edades = {"ana": 30, "eva": 41};
                edades["luis"] = 25;
                edades["ana"] += 1;
                imprimir(edades["ana"]);
                imprimir(edades.cantidad);
                imprimir(edades.contiene("eva"));
                imprimir(edades.quitar("eva"));
                imprimir(edades.contiene("eva"));
                var total = 0;
                para nombre en edades {
                    total += edades[nombre];
                }
                imprimir(total);
                sea claves = edades.claves();
                imprimir(claves[0] + "," + claves[1]);
                sea valores = edades.valores();
                imprimir(valores[0] + valores[1]);
            }
            """);

        Assert.Equal(Lineas("31", "3", "verdadero", "verdadero", "falso", "56", "ana,luis", "56"), resultado.Salida);
    }

    [Fact]
    public void Colecciones_de_objetos_propios_y_colecciones_anidadas()
    {
        var resultado = Ejecutar("""
            clase Persona {
                nombre: texto;
                edad: entero;
            }
            estructura Punto { x: entero; y: entero; }

            funcion mayores(personas: lista<Persona>, minimo: entero): lista<texto> {
                sea nombres: lista<texto> = [];
                para p en personas {
                    si p.edad >= minimo {
                        nombres.agregar(p.nombre);
                    }
                }
                regresar nombres;
            }

            funcion principal() {
                sea gente = [nuevo Persona("ana", 30), nuevo Persona("eva", 17), nuevo Persona("luis", 45)];
                sea adultos = mayores(gente, 18);
                imprimir(adultos.cantidad + ": " + adultos[0] + ", " + adultos[1]);

                sea porNombre = nuevo mapa<texto, Persona>();
                para p en gente { porNombre[p.nombre] = p; }
                porNombre["eva"].edad = 18;
                imprimir(gente[1].edad);

                sea matriz = [[1, 2], [3, 4]];
                matriz[1][0] = 30;
                imprimir(matriz[0][1] + matriz[1][0]);

                sea puntos = [nuevo Punto(1, 2), nuevo Punto(3, 4)];
                var sumaX = 0;
                para p en puntos { sumaX += p.x; }
                imprimir(sumaX);

                sea porClave = {"origen": nuevo Punto(0, 0)};
                imprimir(porClave["origen"].x);
            }
            """);

        Assert.Equal(Lineas("2: ana, luis", "18", "32", "4", "0"), resultado.Salida);
    }

    [Fact]
    public void Metodos_de_texto_y_de_numeros()
    {
        var resultado = Ejecutar("""
            funcion principal() {
                sea s = "  Hola, Lince  ";
                imprimir(s.longitud);
                imprimir(s.recortar().mayusculas());
                imprimir(s.minusculas().contiene("lince"));
                imprimir(s.recortar().empiezaCon("Hola") && s.recortar().terminaCon("ce"));
                imprimir(s.recortar().indiceDe("Lince"));
                imprimir(s.reemplazar("Lince", "mundo").recortar());
                imprimir(s.recortar().subtexto(0, 4));
                sea partes = "a;b;c".partir(";");
                imprimir(partes.cantidad + partes[2]);
                imprimir("42".aEntero() + 1);
                imprimir("2.5".aDecimal() * 2);
                imprimir((-7).absoluto() + 3.absoluto());
                imprimir(2.0.raiz() * 2.0.raiz());
                imprimir(3.7.redondear() + 3.2.piso() + 3.2.techo());
                imprimir(2.0.potencia(10));
                imprimir(9.99.aEntero());
                imprimir(7.aDecimal() / 2);
            }
            """);

        Assert.Equal(Lineas("15", "HOLA, LINCE", "verdadero", "verdadero", "6", "Hola, mundo", "Hola", "3c", "43", "5",
            "10", "2.0000000000000004", "11", "1024", "9", "3.5"), resultado.Salida);
    }

    [Fact]
    public void Para_en_evalua_la_coleccion_una_vez_y_admite_romper_y_continuar()
    {
        var resultado = Ejecutar("""
            funcion numeros(): lista<entero> {
                imprimir("evaluada");
                regresar [1, 2, 3, 4, 5, 6];
            }

            funcion principal() {
                var suma = 0;
                para n en numeros() {
                    si n % 2 == 0 { continuar; }
                    si n > 5 { romper; }
                    suma += n;
                }
                imprimir(suma);
            }
            """);

        Assert.Equal(Lineas("evaluada", "9"), resultado.Salida);
    }
}
