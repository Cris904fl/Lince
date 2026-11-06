using System.Collections.Immutable;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Sintaxis;

/// <summary>
/// Árbol sintáctico inmutable. Cada nodo conserva sus tokens (incluidos los signos de
/// puntuación), así una herramienta futura (formateador, LSP) puede reconstruir el texto y
/// ubicar cualquier parte del programa.
/// </summary>
public abstract record NodoSintaxis
{
    public abstract Ubicacion Ubicacion { get; }
}

// ----- Programa y declaraciones -----

public sealed record ProgramaSintaxis(ArchivoFuente Archivo, ImmutableArray<FuncionSintaxis> Funciones, Token FinDeArchivo) : NodoSintaxis
{
    public override Ubicacion Ubicacion => Funciones.Length == 0
        ? FinDeArchivo.Ubicacion
        : Ubicacion.Entre(Funciones[0].Ubicacion, FinDeArchivo.Ubicacion);
}

public sealed record FuncionSintaxis(
    Token PalabraFuncion,
    Token Nombre,
    Token ParentesisAbre,
    ImmutableArray<ParametroSintaxis> Parametros,
    Token ParentesisCierra,
    TipoSintaxis? TipoRetorno,
    BloqueSintaxis Cuerpo) : NodoSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraFuncion.Ubicacion, Cuerpo.Ubicacion);
}

public sealed record ParametroSintaxis(Token Nombre, Token DosPuntos, TipoSintaxis Tipo) : NodoSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Nombre.Ubicacion, Tipo.Ubicacion);
}

/// <summary>Un nombre de tipo; hoy siempre una palabra clave (entero, decimal, texto, logico, nada).</summary>
public sealed record TipoSintaxis(Token Palabra) : NodoSintaxis
{
    public override Ubicacion Ubicacion => Palabra.Ubicacion;
}

// ----- Sentencias -----

public abstract record SentenciaSintaxis : NodoSintaxis;

public sealed record BloqueSintaxis(Token LlaveAbre, ImmutableArray<SentenciaSintaxis> Sentencias, Token LlaveCierra) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(LlaveAbre.Ubicacion, LlaveCierra.Ubicacion);
}

/// <summary><c>sea x = 1;</c> o <c>var x: entero = 1;</c>.</summary>
public sealed record DeclaracionVariableSintaxis(
    Token Palabra,
    Token Nombre,
    TipoSintaxis? Tipo,
    Token Igual,
    ExpresionSintaxis Valor,
    Token PuntoYComa) : SentenciaSintaxis
{
    public bool EsMutable => Palabra.Tipo == TipoToken.PalabraVar;

    public override Ubicacion Ubicacion => Ubicacion.Entre(Palabra.Ubicacion, PuntoYComa.Ubicacion);
}

/// <summary><c>x = 1;</c> o <c>x += 1;</c>. La asignación es sentencia, no expresión.</summary>
public sealed record AsignacionSintaxis(Token Nombre, Token Operador, ExpresionSintaxis Valor, Token PuntoYComa) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Nombre.Ubicacion, PuntoYComa.Ubicacion);
}

/// <summary><c>si cond { } sino { }</c>. <see cref="Sino"/> es un bloque o otro <c>si</c>.</summary>
public sealed record SiSintaxis(
    Token PalabraSi,
    ExpresionSintaxis Condicion,
    BloqueSintaxis Entonces,
    Token? PalabraSino,
    SentenciaSintaxis? Sino) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraSi.Ubicacion, (Sino ?? (NodoSintaxis)Entonces).Ubicacion);
}

public sealed record MientrasSintaxis(Token PalabraMientras, ExpresionSintaxis Condicion, BloqueSintaxis Cuerpo) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraMientras.Ubicacion, Cuerpo.Ubicacion);
}

/// <summary><c>para i en desde..hasta { }</c>.</summary>
public sealed record ParaSintaxis(
    Token PalabraPara,
    Token Variable,
    Token PalabraEn,
    ExpresionSintaxis Desde,
    Token PuntoPunto,
    ExpresionSintaxis Hasta,
    BloqueSintaxis Cuerpo) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraPara.Ubicacion, Cuerpo.Ubicacion);
}

public sealed record RegresarSintaxis(Token PalabraRegresar, ExpresionSintaxis? Valor, Token PuntoYComa) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraRegresar.Ubicacion, PuntoYComa.Ubicacion);
}

public sealed record RomperSintaxis(Token PalabraRomper, Token PuntoYComa) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraRomper.Ubicacion, PuntoYComa.Ubicacion);
}

public sealed record ContinuarSintaxis(Token PalabraContinuar, Token PuntoYComa) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraContinuar.Ubicacion, PuntoYComa.Ubicacion);
}

public sealed record ExpresionSentenciaSintaxis(ExpresionSintaxis Expresion, Token PuntoYComa) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Expresion.Ubicacion, PuntoYComa.Ubicacion);
}

// ----- Expresiones -----

public abstract record ExpresionSintaxis : NodoSintaxis;

/// <summary>Un entero, decimal, texto, verdadero, falso o nulo.</summary>
public sealed record LiteralSintaxis(Token Literal) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Literal.Ubicacion;
}

public sealed record NombreSintaxis(Token Identificador) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Identificador.Ubicacion;
}

public sealed record UnariaSintaxis(Token Operador, ExpresionSintaxis Operando) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Operador.Ubicacion, Operando.Ubicacion);
}

public sealed record BinariaSintaxis(ExpresionSintaxis Izquierda, Token Operador, ExpresionSintaxis Derecha) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Izquierda.Ubicacion, Derecha.Ubicacion);
}

public sealed record ParentesisSintaxis(Token Abre, ExpresionSintaxis Expresion, Token Cierra) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Abre.Ubicacion, Cierra.Ubicacion);
}

/// <summary><c>objetivo(argumentos)</c>. El objetivo es una expresión para admitir miembros más adelante.</summary>
public sealed record LlamadaSintaxis(
    ExpresionSintaxis Objetivo,
    Token ParentesisAbre,
    ImmutableArray<ExpresionSintaxis> Argumentos,
    Token ParentesisCierra) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Objetivo.Ubicacion, ParentesisCierra.Ubicacion);
}
