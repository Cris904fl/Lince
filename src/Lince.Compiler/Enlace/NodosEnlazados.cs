using System.Collections.Immutable;

namespace Lince.Compiler.Enlace;

/// <summary>
/// Árbol enlazado: el sintáctico ya resuelto, con cada nombre convertido en símbolo y cada
/// expresión con su tipo. Es lo único que lee el emisor de IL, que así no necesita saber
/// nada de la sintaxis.
/// </summary>
public abstract record NodoEnlazado;

public sealed record ProgramaEnlazado(ImmutableArray<FuncionEnlazada> Funciones, SimboloFuncion? Principal) : NodoEnlazado;

public sealed record FuncionEnlazada(SimboloFuncion Simbolo, BloqueEnlazado Cuerpo) : NodoEnlazado;

// ----- Sentencias -----

public abstract record SentenciaEnlazada : NodoEnlazado;

public sealed record BloqueEnlazado(ImmutableArray<SentenciaEnlazada> Sentencias) : SentenciaEnlazada;

public sealed record DeclaracionVariableEnlazada(SimboloVariable Variable, ExpresionEnlazada Valor) : SentenciaEnlazada;

/// <summary>Las asignaciones compuestas (<c>+=</c>) ya vienen desplegadas: el valor es la operación completa.</summary>
public sealed record AsignacionEnlazada(SimboloVariable Variable, ExpresionEnlazada Valor) : SentenciaEnlazada;

public sealed record SiEnlazado(ExpresionEnlazada Condicion, SentenciaEnlazada Entonces, SentenciaEnlazada? Sino) : SentenciaEnlazada;

public sealed record MientrasEnlazado(ExpresionEnlazada Condicion, BloqueEnlazado Cuerpo) : SentenciaEnlazada;

/// <param name="Limite">Temporal oculta donde se guarda <paramref name="Hasta"/> para evaluarlo una sola vez.</param>
public sealed record ParaEnlazado(
    SimboloVariable Variable,
    SimboloVariable Limite,
    ExpresionEnlazada Desde,
    ExpresionEnlazada Hasta,
    BloqueEnlazado Cuerpo) : SentenciaEnlazada;

public sealed record RegresarEnlazado(ExpresionEnlazada? Valor) : SentenciaEnlazada;

public sealed record RomperEnlazado : SentenciaEnlazada;

public sealed record ContinuarEnlazado : SentenciaEnlazada;

public sealed record ExpresionSentenciaEnlazada(ExpresionEnlazada Expresion) : SentenciaEnlazada;

// ----- Expresiones -----

public abstract record ExpresionEnlazada : NodoEnlazado
{
    public abstract TipoLince Tipo { get; }
}

public sealed record LiteralEnlazado(object? Valor, TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

public sealed record VariableEnlazada(SimboloVariable Variable) : ExpresionEnlazada
{
    public override TipoLince Tipo => Variable.Tipo;
}

public enum OperadorUnario
{
    Negativo,
    NegacionLogica,
}

public sealed record UnariaEnlazada(OperadorUnario Operador, ExpresionEnlazada Operando, TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

public enum OperadorBinario
{
    Suma,
    Resta,
    Multiplicacion,
    Division,
    Modulo,
    Igual,
    Distinto,
    Menor,
    MenorIgual,
    Mayor,
    MayorIgual,
    YLogico,
    OLogico,

    /// <summary><c>+</c> entre textos. Ambos operandos ya son <c>texto</c>.</summary>
    Concatenacion,
}

public sealed record BinariaEnlazada(ExpresionEnlazada Izquierda, OperadorBinario Operador, ExpresionEnlazada Derecha, TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

/// <summary>Conversión implícita: entero a decimal, nulo a texto, o cualquier escribible a texto.</summary>
public sealed record ConversionEnlazada(ExpresionEnlazada Expresion, TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

public sealed record LlamadaEnlazada(SimboloFuncion Funcion, ImmutableArray<ExpresionEnlazada> Argumentos) : ExpresionEnlazada
{
    public override TipoLince Tipo => Funcion.TipoRetorno;
}

/// <summary>La integrada <c>imprimir</c>. El valor, si lo hay, ya viene convertido a texto.</summary>
public sealed record ImprimirEnlazado(ExpresionEnlazada? Valor) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoLince.Nada;
}

public sealed record ErrorEnlazado : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoLince.Error;
}
