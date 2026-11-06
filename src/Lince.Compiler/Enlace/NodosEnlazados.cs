using System.Collections.Immutable;

namespace Lince.Compiler.Enlace;

/// <summary>
/// Árbol enlazado: el sintáctico ya resuelto, con cada nombre convertido en símbolo y cada
/// expresión con su tipo. Es lo único que lee el emisor de IL, que así no necesita saber
/// nada de la sintaxis.
/// </summary>
public abstract record NodoEnlazado;

public sealed record ProgramaEnlazado(
    ImmutableArray<FuncionEnlazada> Funciones,
    ImmutableArray<TipoEnlazado> Tipos,
    SimboloFuncion? Principal) : NodoEnlazado;

public sealed record FuncionEnlazada(SimboloFuncion Simbolo, BloqueEnlazado Cuerpo) : NodoEnlazado;

/// <summary>Una clase o estructura con sus valores iniciales de campo, su constructor (si es explícito) y sus métodos.</summary>
public sealed record TipoEnlazado(
    TipoUsuario Tipo,
    ImmutableArray<CampoInicialEnlazado> Iniciales,
    FuncionEnlazada? Constructor,
    ImmutableArray<FuncionEnlazada> Metodos) : NodoEnlazado;

public sealed record CampoInicialEnlazado(SimboloCampo Campo, ExpresionEnlazada Valor) : NodoEnlazado;

// ----- Sentencias -----

public abstract record SentenciaEnlazada : NodoEnlazado;

public sealed record BloqueEnlazado(ImmutableArray<SentenciaEnlazada> Sentencias) : SentenciaEnlazada;

public sealed record DeclaracionVariableEnlazada(SimboloVariable Variable, ExpresionEnlazada Valor) : SentenciaEnlazada;

/// <summary>Las asignaciones compuestas (<c>+=</c>) ya vienen desplegadas: el valor es la operación completa.</summary>
public sealed record AsignacionEnlazada(SimboloVariable Variable, ExpresionEnlazada Valor) : SentenciaEnlazada;

/// <summary><c>objeto.campo = valor;</c>. El objeto es <see cref="EsteEnlazado"/> cuando el campo se nombró a secas dentro de un método.</summary>
public sealed record AsignacionCampoEnlazada(ExpresionEnlazada Objeto, SimboloCampo Campo, ExpresionEnlazada Valor) : SentenciaEnlazada;

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

/// <summary><c>este</c> dentro de un método o constructor.</summary>
public sealed record EsteEnlazado(TipoUsuario TipoUsuario) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoUsuario;
}

/// <summary>Lectura de un campo: <c>objeto.campo</c>.</summary>
public sealed record CampoEnlazado(ExpresionEnlazada Objeto, SimboloCampo Campo) : ExpresionEnlazada
{
    public override TipoLince Tipo => Campo.Tipo;
}

/// <summary><c>nuevo Tipo(argumentos)</c>. Los argumentos ya vienen convertidos a los tipos de los parámetros.</summary>
public sealed record NuevoEnlazado(TipoUsuario TipoUsuario, ImmutableArray<ExpresionEnlazada> Argumentos) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoUsuario;
}

/// <summary>Llamada a un método sobre un objeto: <c>objeto.metodo(argumentos)</c>.</summary>
public sealed record LlamadaMetodoEnlazada(ExpresionEnlazada Objeto, SimboloFuncion Metodo, ImmutableArray<ExpresionEnlazada> Argumentos) : ExpresionEnlazada
{
    public override TipoLince Tipo => Metodo.TipoRetorno;
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

/// <summary>Conversión implícita: entero a decimal, nulo a un tipo por referencia, o cualquier escribible a texto.</summary>
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
