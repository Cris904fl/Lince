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

public sealed record ProgramaSintaxis(ArchivoFuente Archivo, ImmutableArray<DeclaracionSintaxis> Declaraciones, Token FinDeArchivo) : NodoSintaxis
{
    public ImmutableArray<FuncionSintaxis> Funciones => [.. Declaraciones.OfType<FuncionSintaxis>()];

    public ImmutableArray<TipoDeclaradoSintaxis> Tipos => [.. Declaraciones.OfType<TipoDeclaradoSintaxis>()];

    public override Ubicacion Ubicacion => Declaraciones.Length == 0
        ? FinDeArchivo.Ubicacion
        : Ubicacion.Entre(Declaraciones[0].Ubicacion, FinDeArchivo.Ubicacion);
}

/// <summary>Una función, un tipo o un campo: lo que puede aparecer en el nivel superior o dentro de un tipo.</summary>
public abstract record DeclaracionSintaxis : NodoSintaxis;

/// <summary>
/// Una función libre o un método. <see cref="Modificador"/> es <c>privado</c> o <c>publico</c>
/// (solo tiene sentido dentro de un tipo). Un método llamado <c>nuevo</c> es el constructor.
/// </summary>
public sealed record FuncionSintaxis(
    Token? Modificador,
    Token PalabraFuncion,
    Token Nombre,
    Token ParentesisAbre,
    ImmutableArray<ParametroSintaxis> Parametros,
    Token ParentesisCierra,
    TipoSintaxis? TipoRetorno,
    BloqueSintaxis Cuerpo) : DeclaracionSintaxis
{
    public bool EsPrivada => Modificador?.Tipo == TipoToken.PalabraPrivado;

    public bool EsConstructor => Nombre.Tipo == TipoToken.PalabraNuevo;

    public override Ubicacion Ubicacion => Ubicacion.Entre((Modificador ?? PalabraFuncion).Ubicacion, Cuerpo.Ubicacion);
}

/// <summary><c>clase Nombre { miembros }</c> o <c>estructura Nombre { miembros }</c>.</summary>
public sealed record TipoDeclaradoSintaxis(
    Token Palabra,
    Token Nombre,
    Token LlaveAbre,
    ImmutableArray<DeclaracionSintaxis> Miembros,
    Token LlaveCierra) : DeclaracionSintaxis
{
    public bool EsEstructura => Palabra.Tipo == TipoToken.PalabraEstructura;

    public ImmutableArray<CampoSintaxis> Campos => [.. Miembros.OfType<CampoSintaxis>()];

    public ImmutableArray<FuncionSintaxis> Metodos => [.. Miembros.OfType<FuncionSintaxis>()];

    public override Ubicacion Ubicacion => Ubicacion.Entre(Palabra.Ubicacion, LlaveCierra.Ubicacion);
}

/// <summary><c>nombre: tipo;</c> o <c>nombre: tipo = valor;</c> dentro de un tipo.</summary>
public sealed record CampoSintaxis(
    Token? Modificador,
    Token Nombre,
    Token DosPuntos,
    TipoSintaxis Tipo,
    Token? Igual,
    ExpresionSintaxis? Inicial,
    Token PuntoYComa) : DeclaracionSintaxis
{
    public bool EsPrivado => Modificador?.Tipo == TipoToken.PalabraPrivado;

    public override Ubicacion Ubicacion => Ubicacion.Entre((Modificador ?? Nombre).Ubicacion, PuntoYComa.Ubicacion);
}

public sealed record ParametroSintaxis(Token Nombre, Token DosPuntos, TipoSintaxis Tipo) : NodoSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Nombre.Ubicacion, Tipo.Ubicacion);
}

/// <summary>Un nombre de tipo: una palabra clave (entero, decimal, texto, logico, nada) o el identificador de un tipo declarado.</summary>
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

/// <summary>
/// <c>x = 1;</c>, <c>x += 1;</c> o <c>objeto.campo = 1;</c>. La asignación es sentencia, no
/// expresión; el objetivo es un nombre o un acceso a miembro.
/// </summary>
public sealed record AsignacionSintaxis(ExpresionSintaxis Objetivo, Token Operador, ExpresionSintaxis Valor, Token PuntoYComa) : SentenciaSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Objetivo.Ubicacion, PuntoYComa.Ubicacion);
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

/// <summary>La palabra <c>este</c>: el objeto sobre el que corre el método.</summary>
public sealed record EsteSintaxis(Token Palabra) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Palabra.Ubicacion;
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

/// <summary><c>objetivo(argumentos)</c>: llamada a una función libre (el objetivo es un nombre) o a un método (es un miembro).</summary>
public sealed record LlamadaSintaxis(
    ExpresionSintaxis Objetivo,
    Token ParentesisAbre,
    ImmutableArray<ExpresionSintaxis> Argumentos,
    Token ParentesisCierra) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Objetivo.Ubicacion, ParentesisCierra.Ubicacion);
}

/// <summary><c>objeto.nombre</c>: un campo, o un método cuando es el objetivo de una llamada.</summary>
public sealed record MiembroSintaxis(ExpresionSintaxis Objeto, Token Punto, Token Nombre) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(Objeto.Ubicacion, Nombre.Ubicacion);
}

/// <summary><c>nuevo Tipo(argumentos)</c>.</summary>
public sealed record NuevoSintaxis(
    Token PalabraNuevo,
    Token Tipo,
    Token ParentesisAbre,
    ImmutableArray<ExpresionSintaxis> Argumentos,
    Token ParentesisCierra) : ExpresionSintaxis
{
    public override Ubicacion Ubicacion => Ubicacion.Entre(PalabraNuevo.Ubicacion, ParentesisCierra.Ubicacion);
}
