using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Sintaxis;

namespace Lince.Tests;

public class PruebasDeSintaxis
{
    /// <summary>Analiza una expresión suelta envolviéndola en una función.</summary>
    private static ExpresionSintaxis Expresion(string codigo)
    {
        var arbol = ArbolSintaxis.Analizar($"funcion f() {{ x = {codigo}; }}");
        Assert.Empty(arbol.Diagnosticos);
        var asignacion = Assert.IsType<AsignacionSintaxis>(Assert.Single(arbol.Raiz.Funciones[0].Cuerpo.Sentencias));
        return asignacion.Valor;
    }

    /// <summary>Representación compacta de una expresión para comparar la estructura del árbol.</summary>
    private static string Plano(ExpresionSintaxis expresion) => expresion switch
    {
        LiteralSintaxis l => l.Literal.Texto,
        NombreSintaxis n => n.Identificador.Texto,
        UnariaSintaxis u => $"({u.Operador.Texto}{Plano(u.Operando)})",
        BinariaSintaxis b => $"({Plano(b.Izquierda)} {b.Operador.Texto} {Plano(b.Derecha)})",
        ParentesisSintaxis p => Plano(p.Expresion),
        LlamadaSintaxis ll => $"{Plano(ll.Objetivo)}[{string.Join(", ", ll.Argumentos.Select(Plano))}]",
        _ => throw new InvalidOperationException(),
    };

    [Fact]
    public void Analiza_una_funcion_con_parametros_y_tipo_de_retorno()
    {
        var arbol = ArbolSintaxis.Analizar("funcion sumar(a: entero, b: decimal): decimal { regresar a + b; }");

        Assert.Empty(arbol.Diagnosticos);
        var funcion = Assert.Single(arbol.Raiz.Funciones);
        Assert.Equal("sumar", funcion.Nombre.Texto);
        Assert.Equal(["a", "b"], funcion.Parametros.Select(p => p.Nombre.Texto));
        Assert.Equal(TipoToken.PalabraDecimal, funcion.Parametros[1].Tipo.Palabra.Tipo);
        Assert.Equal(TipoToken.PalabraDecimal, funcion.TipoRetorno!.Palabra.Tipo);
        var regresar = Assert.IsType<RegresarSintaxis>(Assert.Single(funcion.Cuerpo.Sentencias));
        Assert.NotNull(regresar.Valor);
    }

    [Fact]
    public void Una_funcion_sin_dos_puntos_no_tiene_tipo_de_retorno()
    {
        var arbol = ArbolSintaxis.Analizar("funcion principal() { }");

        Assert.Empty(arbol.Diagnosticos);
        Assert.Null(arbol.Raiz.Funciones[0].TipoRetorno);
    }

    [Theory]
    [InlineData("1 + 2 * 3", "(1 + (2 * 3))")]
    [InlineData("1 * 2 + 3", "((1 * 2) + 3)")]
    [InlineData("(1 + 2) * 3", "((1 + 2) * 3)")]
    [InlineData("a - b - c", "((a - b) - c)")]
    [InlineData("a < b == c > d", "((a < b) == (c > d))")]
    [InlineData("a || b && c", "(a || (b && c))")]
    [InlineData("!a && -b", "((!a) && (-b))")]
    [InlineData("- - a", "(-(-a))")]
    [InlineData("f(1, g(2)) + 3", "(f[1, g[2]] + 3)")]
    [InlineData("a % b / c", "((a % b) / c)")]
    public void Respeta_la_precedencia_y_la_asociatividad(string codigo, string esperado)
    {
        Assert.Equal(esperado, Plano(Expresion(codigo)));
    }

    [Fact]
    public void Analiza_si_sino_si_sino_como_cadena()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() { si a { } sino si b { } sino { } }");

        Assert.Empty(arbol.Diagnosticos);
        var si = Assert.IsType<SiSintaxis>(Assert.Single(arbol.Raiz.Funciones[0].Cuerpo.Sentencias));
        var segundo = Assert.IsType<SiSintaxis>(si.Sino);
        Assert.IsType<BloqueSintaxis>(segundo.Sino);
    }

    [Fact]
    public void Analiza_para_mientras_declaraciones_y_asignaciones_compuestas()
    {
        var arbol = ArbolSintaxis.Analizar("""
            funcion f() {
                sea a: entero = 1;
                var b = 2;
                para i en 0..a { b += i; }
                mientras b > 0 { b -= 1; romper; continuar; }
                imprimir(b);
            }
            """);

        Assert.Empty(arbol.Diagnosticos);
        var sentencias = arbol.Raiz.Funciones[0].Cuerpo.Sentencias;
        var sea = Assert.IsType<DeclaracionVariableSintaxis>(sentencias[0]);
        Assert.False(sea.EsMutable);
        Assert.NotNull(sea.Tipo);
        var var = Assert.IsType<DeclaracionVariableSintaxis>(sentencias[1]);
        Assert.True(var.EsMutable);
        Assert.Null(var.Tipo);
        var para = Assert.IsType<ParaSintaxis>(sentencias[2]);
        Assert.Equal("i", para.Variable.Texto);
        Assert.Equal(TipoToken.MasIgual, Assert.IsType<AsignacionSintaxis>(para.Cuerpo.Sentencias[0]).Operador.Tipo);
        var mientras = Assert.IsType<MientrasSintaxis>(sentencias[3]);
        Assert.IsType<RomperSintaxis>(mientras.Cuerpo.Sentencias[1]);
        Assert.IsType<ContinuarSintaxis>(mientras.Cuerpo.Sentencias[2]);
        Assert.IsType<ExpresionSentenciaSintaxis>(sentencias[4]);
    }

    [Fact]
    public void Un_punto_y_coma_faltante_se_informa_con_linea_y_columna_y_se_sigue_analizando()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() {\n    sea a = 1\n    sea b = 2;\n}", "prueba.ln");

        var error = Assert.Single(arbol.Diagnosticos);
        Assert.Equal(Codigos.TokenInesperado, error.Codigo);
        Assert.Equal("prueba.ln(3,5): error LN2001: se esperaba ';' pero se encontró 'sea'", error.ToString());

        // La recuperación sintetiza el ';' y la segunda declaración queda en el árbol.
        var sentencias = arbol.Raiz.Funciones[0].Cuerpo.Sentencias;
        Assert.Equal(2, sentencias.Length);
        Assert.True(Assert.IsType<DeclaracionVariableSintaxis>(sentencias[0]).PuntoYComa.EsFaltante);
        Assert.False(Assert.IsType<DeclaracionVariableSintaxis>(sentencias[1]).PuntoYComa.EsFaltante);
    }

    [Fact]
    public void Varios_errores_en_un_archivo_se_informan_todos_en_una_pasada()
    {
        var arbol = ArbolSintaxis.Analizar("""
            funcion f() {
                sea = 1;
                var b = ;
                g(1, ;
            }
            funcion h( { }
            """);

        Assert.Equal(4, arbol.Diagnosticos.Length);
        Assert.Equal([2, 3, 4, 6], arbol.Diagnosticos.Select(d => d.Linea));
        Assert.Equal(2, arbol.Raiz.Funciones.Length);
    }

    [Fact]
    public void Una_palabra_reservada_da_LN2004()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() { clase x = 1; }");

        Assert.Contains(arbol.Diagnosticos, d => d.Codigo == Codigos.PalabraReservada && d.Mensaje.Contains("'clase'"));
    }

    [Fact]
    public void Fuera_de_una_funcion_solo_puede_haber_funciones()
    {
        var arbol = ArbolSintaxis.Analizar("sea x = 1;\nfuncion principal() { }");

        var error = Assert.Single(arbol.Diagnosticos);
        Assert.Equal(Codigos.SeEsperabaDeclaracion, error.Codigo);
        Assert.Contains("dentro de una función", error.Mensaje);
        Assert.Single(arbol.Raiz.Funciones);
    }

    [Fact]
    public void Un_tipo_desconocido_da_LN2005()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f(a: numero) { }");

        var error = Assert.Single(arbol.Diagnosticos);
        Assert.Equal(Codigos.SeEsperabaTipo, error.Codigo);
        Assert.Equal(14, error.Columna);
    }

    [Fact]
    public void Cada_nodo_conoce_su_ubicacion_completa()
    {
        var arbol = ArbolSintaxis.Analizar("funcion f() { regresar 1 + 2; }");

        var funcion = arbol.Raiz.Funciones[0];
        Assert.Equal("funcion f() { regresar 1 + 2; }", funcion.Ubicacion.Texto);
        var regresar = (RegresarSintaxis)funcion.Cuerpo.Sentencias[0];
        Assert.Equal("regresar 1 + 2;", regresar.Ubicacion.Texto);
        Assert.Equal("1 + 2", regresar.Valor!.Ubicacion.Texto);
    }
}
