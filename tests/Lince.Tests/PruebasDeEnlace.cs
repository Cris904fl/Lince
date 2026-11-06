using System.Collections.Immutable;
using Lince.Compiler;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Enlace;
using Lince.Compiler.Texto;

namespace Lince.Tests;

/// <summary>Pruebas del enlazador: cada error de tipos esperado y la inferencia local.</summary>
public class PruebasDeEnlace
{
    private static (ProgramaEnlazado Programa, ImmutableArray<Diagnostico> Diagnosticos) Enlazar(string codigo) =>
        Compilador.Enlazar(ArchivoFuente.DesdeTexto(codigo, "prueba.ln"));

    /// <summary>Enlaza el cuerpo dado dentro de <c>principal</c> y devuelve el único error esperado.</summary>
    private static Diagnostico UnicoError(string cuerpoDePrincipal, string? otrasFunciones = null)
    {
        var (_, diagnosticos) = Enlazar($"{otrasFunciones}\nfuncion principal() {{\n{cuerpoDePrincipal}\n}}");
        return Assert.Single(diagnosticos);
    }

    [Fact]
    public void Un_programa_correcto_no_tiene_diagnosticos()
    {
        var (programa, diagnosticos) = Enlazar("""
            funcion doble(n: entero): entero { regresar n * 2; }
            funcion principal() {
                sea a = doble(4);
                var b: decimal = a;
                b += 0.5;
                si a > 1 && b < 10 { imprimir("a: " + a + ", b: " + b); } sino { imprimir(falso); }
                para i en 0..a { si i == 2 { continuar; } }
                mientras b > 0 { b -= 1; si b < 2 { romper; } }
            }
            """);

        Assert.Empty(diagnosticos);
        Assert.NotNull(programa.Principal);
        Assert.Equal(2, programa.Funciones.Length);
    }

    [Fact]
    public void Infiere_el_tipo_de_una_variable_desde_su_valor()
    {
        var (programa, diagnosticos) = Enlazar("""
            funcion principal() {
                sea a = 1 + 2;
                sea b = 1 + 2.5;
                sea c = "x" + a;
                sea d = a < 3;
            }
            """);

        Assert.Empty(diagnosticos);
        var cuerpo = programa.Funciones[0].Cuerpo.Sentencias.Cast<DeclaracionVariableEnlazada>().ToList();
        Assert.Equal(TipoLince.Entero, cuerpo[0].Variable.Tipo);
        Assert.Equal(TipoLince.Decimal, cuerpo[1].Variable.Tipo);
        Assert.Equal(TipoLince.Texto, cuerpo[2].Variable.Tipo);
        Assert.Equal(TipoLince.Logico, cuerpo[3].Variable.Tipo);

        // El entero del segundo caso se convierte a decimal antes de sumar.
        var suma = Assert.IsType<BinariaEnlazada>(cuerpo[1].Valor);
        Assert.IsType<ConversionEnlazada>(suma.Izquierda);
    }

    [Fact]
    public void Sin_principal_es_LN3003()
    {
        var (_, diagnosticos) = Enlazar("funcion f() { }");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.FaltaPrincipal, error.Codigo);
    }

    [Theory]
    [InlineData("funcion principal(a: entero) { }")]
    [InlineData("funcion principal(): texto { regresar \"x\"; }")]
    public void Principal_mal_formada_es_LN3004(string codigo)
    {
        var (_, diagnosticos) = Enlazar(codigo);

        Assert.Equal(Codigos.PrincipalMalFormada, Assert.Single(diagnosticos).Codigo);
    }

    [Fact]
    public void Dos_funciones_con_el_mismo_nombre_es_LN3001()
    {
        var error = UnicoError("", "funcion f() { }\nfuncion f() { }");

        Assert.Equal(Codigos.FuncionDuplicada, error.Codigo);
        Assert.Equal(2, error.Linea);
    }

    [Fact]
    public void Dos_parametros_con_el_mismo_nombre_es_LN3002()
    {
        Assert.Equal(Codigos.ParametroDuplicado, UnicoError("", "funcion f(a: entero, a: entero) { }").Codigo);
    }

    [Fact]
    public void Declarar_imprimir_es_LN3005()
    {
        Assert.Equal(Codigos.NombreDeIntegrada, UnicoError("", "funcion imprimir(a: entero) { }").Codigo);
    }

    [Fact]
    public void Variable_repetida_en_el_mismo_ambito_o_en_uno_exterior_es_LN3010()
    {
        Assert.Equal(Codigos.VariableDuplicada, UnicoError("sea a = 1; sea a = 2;").Codigo);
        Assert.Equal(Codigos.VariableDuplicada, UnicoError("sea a = 1; { sea a = 2; }").Codigo);
    }

    [Fact]
    public void Bloques_hermanos_pueden_reutilizar_un_nombre()
    {
        var (_, diagnosticos) = Enlazar("funcion principal() { { sea a = 1; } { sea a = 2; } }");

        Assert.Empty(diagnosticos);
    }

    [Fact]
    public void Variable_inexistente_es_LN3011_con_su_ubicacion()
    {
        var error = UnicoError("imprimir(contadr);");

        Assert.Equal(Codigos.VariableNoExiste, error.Codigo);
        Assert.Equal("prueba.ln(3,10): error LN3011: no existe una variable llamada 'contadr'", error.ToString());
    }

    [Fact]
    public void Funcion_inexistente_es_LN3012()
    {
        Assert.Equal(Codigos.FuncionNoExiste, UnicoError("saludar();").Codigo);
    }

    [Fact]
    public void Cantidad_de_argumentos_equivocada_es_LN3013()
    {
        var error = UnicoError("f(1, 2);", "funcion f(a: entero) { }");

        Assert.Equal(Codigos.CantidadDeArgumentos, error.Codigo);
        Assert.Contains("espera 1 argumento y recibió 2", error.Mensaje);
    }

    [Fact]
    public void Tipo_de_argumento_equivocado_es_LN3014()
    {
        var error = UnicoError("f(\"uno\");", "funcion f(a: entero) { }");

        Assert.Equal(Codigos.TipoDeArgumento, error.Codigo);
        Assert.Contains("debe ser 'entero' y este es 'texto'", error.Mensaje);
    }

    [Theory]
    [InlineData("sea a = 1; a = 2;", "'sea'")]
    [InlineData("para i en 0..3 { i = 2; }", "'para'")]
    public void Asignar_a_una_variable_inmutable_es_LN3015(string cuerpo, string pista)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.AsignacionAInmutable, error.Codigo);
        Assert.Contains(pista, error.Mensaje);
    }

    [Fact]
    public void Asignar_a_un_parametro_es_LN3015()
    {
        var error = UnicoError("", "funcion f(a: entero) { a = 2; }");

        Assert.Equal(Codigos.AsignacionAInmutable, error.Codigo);
        Assert.Contains("parámetro", error.Mensaje);
    }

    [Theory]
    [InlineData("var a = 1; a = \"x\";")]
    [InlineData("sea a: entero = \"x\";")]
    [InlineData("sea a: logico = 1;")]
    [InlineData("var a: entero = 1; a = 2.5;")]
    public void Tipos_incompatibles_es_LN3016(string cuerpo)
    {
        Assert.Equal(Codigos.TipoIncompatible, UnicoError(cuerpo).Codigo);
    }

    [Theory]
    [InlineData("si 1 { }")]
    [InlineData("mientras \"x\" { }")]
    public void Condicion_no_logica_es_LN3017(string cuerpo)
    {
        Assert.Equal(Codigos.CondicionNoLogica, UnicoError(cuerpo).Codigo);
    }

    [Theory]
    [InlineData("sea a = 1 + verdadero;", "'+'", "'entero' y 'logico'")]
    [InlineData("sea a = \"x\" - 1;", "'-'", "'texto' y 'entero'")]
    [InlineData("sea a = \"x\" < \"y\";", "'<'", "'texto' y 'texto'")]
    [InlineData("sea a = 1 && verdadero;", "'&&'", "'entero' y 'logico'")]
    [InlineData("sea a = 1 == \"1\";", "'=='", "'entero' y 'texto'")]
    public void Operador_binario_no_definido_es_LN3018(string cuerpo, string operador, string tipos)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.OperadorBinarioInvalido, error.Codigo);
        Assert.Contains(operador, error.Mensaje);
        Assert.Contains(tipos, error.Mensaje);
    }

    [Theory]
    [InlineData("sea a = -\"x\";")]
    [InlineData("sea a = !1;")]
    public void Operador_unario_no_definido_es_LN3019(string cuerpo)
    {
        Assert.Equal(Codigos.OperadorUnarioInvalido, UnicoError(cuerpo).Codigo);
    }

    [Fact]
    public void Regresar_con_valor_en_funcion_nada_es_LN3020()
    {
        Assert.Equal(Codigos.RegresarConValorEnNada, UnicoError("regresar 1;").Codigo);
    }

    [Fact]
    public void Regresar_sin_valor_en_funcion_con_tipo_es_LN3021()
    {
        Assert.Equal(Codigos.RegresarSinValor, UnicoError("", "funcion f(): entero { regresar; }").Codigo);
    }

    [Fact]
    public void Regresar_un_tipo_distinto_es_LN3022()
    {
        var error = UnicoError("", "funcion f(): entero { regresar \"x\"; }");

        Assert.Equal(Codigos.TipoDeRetorno, error.Codigo);
    }

    [Fact]
    public void Regresar_un_entero_donde_se_espera_decimal_se_convierte()
    {
        var (_, diagnosticos) = Enlazar("funcion f(): decimal { regresar 1; }\nfuncion principal() { }");

        Assert.Empty(diagnosticos);
    }

    [Theory]
    [InlineData("romper;")]
    [InlineData("si verdadero { continuar; }")]
    public void Romper_o_continuar_fuera_de_un_ciclo_es_LN3023(string cuerpo)
    {
        Assert.Equal(Codigos.FueraDeCiclo, UnicoError(cuerpo).Codigo);
    }

    [Theory]
    [InlineData("1 + 2;")]
    [InlineData("sea a = 1; a;")]
    public void Una_expresion_sin_efecto_como_sentencia_es_LN3024(string cuerpo)
    {
        Assert.Equal(Codigos.ExpresionSinEfecto, UnicoError(cuerpo).Codigo);
    }

    [Theory]
    [InlineData("funcion f(): entero { }")]
    [InlineData("funcion f(a: logico): entero { si a { regresar 1; } }")]
    [InlineData("funcion f(a: logico): entero { mientras a { regresar 1; } }")]
    [InlineData("funcion f(): entero { mientras verdadero { romper; } }")]
    public void Si_no_todos_los_caminos_regresan_es_LN3025(string funcion)
    {
        var error = UnicoError("", funcion);

        Assert.Equal(Codigos.NoTodosLosCaminosRegresan, error.Codigo);
        Assert.Equal(1, error.Linea);
    }

    [Theory]
    [InlineData("funcion f(a: logico): entero { si a { regresar 1; } sino { regresar 2; } }")]
    [InlineData("funcion f(a: logico): entero { si a { regresar 1; } sino si !a { regresar 2; } sino { regresar 3; } }")]
    [InlineData("funcion f(): entero { mientras verdadero { } }")]
    [InlineData("funcion f(a: logico): entero { { regresar 1; } }")]
    public void Reconoce_cuando_todos_los_caminos_regresan(string funcion)
    {
        var (_, diagnosticos) = Enlazar($"{funcion}\nfuncion principal() {{ }}");

        Assert.Empty(diagnosticos);
    }

    [Theory]
    [InlineData("sea a = principal;", "es una función, no una variable")]
    [InlineData("sea a = 1; a();", "es una variable, no una función")]
    public void Confundir_funcion_y_variable_es_LN3026(string cuerpo, string mensaje)
    {
        var error = UnicoError(cuerpo);

        Assert.Equal(Codigos.NombreNoEsVariable, error.Codigo);
        Assert.Contains(mensaje, error.Mensaje);
    }

    [Fact]
    public void Nulo_en_un_tipo_que_no_lo_admite_es_LN3027()
    {
        Assert.Equal(Codigos.NuloEnTipoNoNulable, UnicoError("sea a: entero = nulo;").Codigo);
    }

    [Fact]
    public void Nulo_en_texto_es_valido_y_se_puede_comparar()
    {
        var (_, diagnosticos) = Enlazar("funcion principal() { sea a: texto? = nulo; si a == nulo { imprimir(\"vacío\"); } }");

        Assert.Empty(diagnosticos);
    }

    [Fact]
    public void Limites_de_para_no_enteros_es_LN3028()
    {
        Assert.Equal(Codigos.LimiteDeParaNoEntero, UnicoError("para i en 0..2.5 { }").Codigo);
    }

    [Fact]
    public void Inferir_desde_nulo_es_LN3029()
    {
        var error = UnicoError("sea a = nulo;");

        Assert.Equal(Codigos.NoSePuedeInferir, error.Codigo);
        Assert.Contains("sea a: texto? = nulo;", error.Mensaje);
    }

    [Theory]
    [InlineData("sea a = f();")]
    [InlineData("imprimir(f());")]
    public void Usar_como_valor_una_funcion_que_regresa_nada_es_LN3030(string cuerpo)
    {
        Assert.Equal(Codigos.ExpresionSinValor, UnicoError(cuerpo, "funcion f() { }").Codigo);
    }

    [Fact]
    public void Imprimir_con_varios_argumentos_es_LN3013_con_pista()
    {
        var error = UnicoError("imprimir(1, 2);");

        Assert.Equal(Codigos.CantidadDeArgumentos, error.Codigo);
        Assert.Contains("concatene con '+'", error.Mensaje);
    }

    [Fact]
    public void Un_error_de_tipos_no_se_repite_en_cascada()
    {
        // El 'y' inexistente es un solo error; las operaciones que lo usan no añaden más.
        var error = UnicoError("sea a = ((y + 1) * 2 + 3) / 4 == 1;");

        Assert.Equal(Codigos.VariableNoExiste, error.Codigo);
    }

    [Fact]
    public void Un_error_de_sintaxis_no_genera_errores_de_tipos_sobre_lo_que_falto()
    {
        var (_, diagnosticos) = Enlazar("funcion principal() { sea a = ; imprimir(a); }");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.SeEsperabaExpresion, error.Codigo);
    }
}
