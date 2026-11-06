using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Tests;

public class PruebasDeLexico
{
    private static (List<Token> Tokens, List<Diagnostico> Diagnosticos) Tokenizar(string codigo)
    {
        var bolsa = new BolsaDiagnosticos();
        var tokens = AnalizadorLexico.Tokenizar(ArchivoFuente.DesdeTexto(codigo), bolsa);
        return (tokens.ToList(), bolsa.ToList());
    }

    private static List<TipoToken> Tipos(string codigo) => Tokenizar(codigo).Tokens.Select(t => t.Tipo).ToList();

    [Fact]
    public void Reconoce_palabras_clave_identificadores_y_signos()
    {
        var tipos = Tipos("funcion sumar(a: entero, b: entero): entero { regresar a + b; }");

        Assert.Equal(
        [
            TipoToken.PalabraFuncion, TipoToken.Identificador, TipoToken.ParentesisAbre,
            TipoToken.Identificador, TipoToken.DosPuntos, TipoToken.PalabraEntero, TipoToken.Coma,
            TipoToken.Identificador, TipoToken.DosPuntos, TipoToken.PalabraEntero, TipoToken.ParentesisCierra,
            TipoToken.DosPuntos, TipoToken.PalabraEntero, TipoToken.LlaveAbre,
            TipoToken.PalabraRegresar, TipoToken.Identificador, TipoToken.Mas, TipoToken.Identificador, TipoToken.PuntoYComa,
            TipoToken.LlaveCierra, TipoToken.FinDeArchivo,
        ], tipos);
    }

    [Theory]
    [InlineData("sea", TipoToken.PalabraSea)]
    [InlineData("var", TipoToken.PalabraVar)]
    [InlineData("si", TipoToken.PalabraSi)]
    [InlineData("sino", TipoToken.PalabraSino)]
    [InlineData("mientras", TipoToken.PalabraMientras)]
    [InlineData("para", TipoToken.PalabraPara)]
    [InlineData("en", TipoToken.PalabraEn)]
    [InlineData("romper", TipoToken.PalabraRomper)]
    [InlineData("continuar", TipoToken.PalabraContinuar)]
    [InlineData("verdadero", TipoToken.PalabraVerdadero)]
    [InlineData("falso", TipoToken.PalabraFalso)]
    [InlineData("nulo", TipoToken.PalabraNulo)]
    [InlineData("decimal", TipoToken.PalabraDecimal)]
    [InlineData("texto", TipoToken.PalabraTexto)]
    [InlineData("logico", TipoToken.PalabraLogico)]
    [InlineData("nada", TipoToken.PalabraNada)]
    [InlineData("clase", TipoToken.PalabraClase)]
    [InlineData("estructura", TipoToken.PalabraEstructura)]
    [InlineData("nuevo", TipoToken.PalabraNuevo)]
    [InlineData("este", TipoToken.PalabraEste)]
    [InlineData("privado", TipoToken.PalabraPrivado)]
    [InlineData("importar", TipoToken.PalabraReservada)]
    [InlineData("Sea", TipoToken.Identificador)]
    public void Distingue_cada_palabra_clave(string texto, TipoToken esperado)
    {
        Assert.Equal(esperado, Tipos(texto)[0]);
    }

    [Theory]
    [InlineData("+ - * / %", new[] { TipoToken.Mas, TipoToken.Menos, TipoToken.Asterisco, TipoToken.Barra, TipoToken.Porcentaje })]
    [InlineData("= += -= *= /= %=", new[] { TipoToken.Igual, TipoToken.MasIgual, TipoToken.MenosIgual, TipoToken.AsteriscoIgual, TipoToken.BarraIgual, TipoToken.PorcentajeIgual })]
    [InlineData("== != < <= > >=", new[] { TipoToken.IgualIgual, TipoToken.Distinto, TipoToken.Menor, TipoToken.MenorIgual, TipoToken.Mayor, TipoToken.MayorIgual })]
    [InlineData("&& || !", new[] { TipoToken.YLogico, TipoToken.OLogico, TipoToken.Negacion })]
    [InlineData("{ } ( ) , ; : ..", new[] { TipoToken.LlaveAbre, TipoToken.LlaveCierra, TipoToken.ParentesisAbre, TipoToken.ParentesisCierra, TipoToken.Coma, TipoToken.PuntoYComa, TipoToken.DosPuntos, TipoToken.PuntoPunto })]
    public void Reconoce_los_operadores_de_uno_y_dos_caracteres(string codigo, TipoToken[] esperados)
    {
        var tipos = Tipos(codigo);
        tipos.RemoveAt(tipos.Count - 1); // FinDeArchivo
        Assert.Equal(esperados, tipos);
    }

    [Fact]
    public void Lee_literales_enteros_con_separador_y_decimales()
    {
        var (tokens, diagnosticos) = Tokenizar("42 1_000_000 3.14 0.5");

        Assert.Empty(diagnosticos);
        Assert.Equal(42L, tokens[0].Valor);
        Assert.Equal(1_000_000L, tokens[1].Valor);
        Assert.Equal(TipoToken.LiteralDecimal, tokens[2].Tipo);
        Assert.Equal(3.14, tokens[2].Valor);
        Assert.Equal(0.5, tokens[3].Valor);
    }

    [Fact]
    public void Un_rango_de_enteros_no_se_confunde_con_un_decimal()
    {
        var tipos = Tipos("0..10");

        Assert.Equal([TipoToken.LiteralEntero, TipoToken.PuntoPunto, TipoToken.LiteralEntero, TipoToken.FinDeArchivo], tipos);
    }

    [Fact]
    public void Un_entero_que_no_cabe_en_64_bits_es_error_LN1003()
    {
        var (tokens, diagnosticos) = Tokenizar("99999999999999999999");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.NumeroFueraDeRango, error.Codigo);
        Assert.Equal(TipoToken.Invalido, tokens[0].Tipo);
    }

    [Fact]
    public void Lee_textos_con_escapes()
    {
        var (tokens, diagnosticos) = Tokenizar("\"hola\\n\\t\\\"mundo\\\"\\\\\"");

        Assert.Empty(diagnosticos);
        Assert.Equal(TipoToken.LiteralTexto, tokens[0].Tipo);
        Assert.Equal("hola\n\t\"mundo\"\\", tokens[0].Valor);
    }

    [Fact]
    public void Un_texto_sin_cerrar_es_error_LN1001_y_el_resto_se_sigue_leyendo()
    {
        var (tokens, diagnosticos) = Tokenizar("\"sin cerrar\nsea");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.TextoSinCerrar, error.Codigo);
        Assert.Equal(1, error.Linea);
        Assert.Equal(1, error.Columna);
        Assert.Equal(TipoToken.PalabraSea, tokens[1].Tipo);
    }

    [Fact]
    public void Un_escape_desconocido_es_error_LN1002()
    {
        var (_, diagnosticos) = Tokenizar("\"a\\qb\"");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.EscapeDesconocido, error.Codigo);
        Assert.Equal(3, error.Columna);
    }

    [Fact]
    public void Salta_comentarios_de_linea_y_de_bloque()
    {
        var tipos = Tipos("sea // comentario\n/* bloque\n de varias líneas */ var");

        Assert.Equal([TipoToken.PalabraSea, TipoToken.PalabraVar, TipoToken.FinDeArchivo], tipos);
    }

    [Fact]
    public void Un_comentario_de_bloque_sin_cerrar_es_error_LN1004()
    {
        var (_, diagnosticos) = Tokenizar("sea /* abierto");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.ComentarioSinCerrar, error.Codigo);
        Assert.Equal(5, error.Columna);
    }

    [Fact]
    public void Un_caracter_desconocido_es_error_LN1005_con_pista_para_el_ampersand()
    {
        var (tokens, diagnosticos) = Tokenizar("a & b");

        var error = Assert.Single(diagnosticos);
        Assert.Equal(Codigos.CaracterInesperado, error.Codigo);
        Assert.Contains("'&&'", error.Mensaje);
        Assert.Equal(TipoToken.Invalido, tokens[1].Tipo);
        Assert.Equal(TipoToken.Identificador, tokens[2].Tipo);
    }

    [Fact]
    public void Admite_identificadores_con_enie_y_tildes()
    {
        var (tokens, _) = Tokenizar("año niñoGrande _privado señal2");

        Assert.All(tokens.Take(4), t => Assert.Equal(TipoToken.Identificador, t.Tipo));
        Assert.Equal("año", tokens[0].Texto);
    }

    [Fact]
    public void Cada_token_sabe_su_linea_y_columna()
    {
        var (tokens, _) = Tokenizar("sea x = 1;\n  var y = 2;\r\nz");

        Assert.Equal((1, 1), (tokens[0].Linea, tokens[0].Columna)); // sea
        Assert.Equal((1, 5), (tokens[1].Linea, tokens[1].Columna)); // x
        Assert.Equal((2, 3), (tokens[5].Linea, tokens[5].Columna)); // var
        Assert.Equal((3, 1), (tokens[10].Linea, tokens[10].Columna)); // z
    }
}
