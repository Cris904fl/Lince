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

/// <param name="Lambdas">Las lambdas que contiene el cuerpo y lo que capturan; null si no hay ninguna.</param>
public sealed record FuncionEnlazada(SimboloFuncion Simbolo, BloqueEnlazado Cuerpo, InfoDeLambdas? Lambdas = null) : NodoEnlazado;

/// <summary>
/// Lo que el emisor necesita saber de las lambdas de un cuerpo: cuáles son (en orden de
/// aparición, incluidas las anidadas), qué variables del cuerpo capturan y si capturan
/// <c>este</c>. Con capturas, todas van a una clase de cierre; sin ellas, son métodos estáticos.
/// </summary>
public sealed record InfoDeLambdas(ImmutableArray<LambdaEnlazada> Lambdas, ImmutableArray<SimboloVariable> Capturadas, bool CapturaEste)
{
    public bool NecesitaCierre => Capturadas.Length > 0 || CapturaEste;
}

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

/// <summary>
/// <c>para x en coleccion { }</c>. La colección se guarda una vez en <paramref name="Coleccion"/>
/// y se recorre por índice (<paramref name="Indice"/>); en un mapa, la colección es su lista de claves.
/// </summary>
public sealed record ParaEnColeccionEnlazado(
    SimboloVariable Variable,
    SimboloVariable Coleccion,
    SimboloVariable Indice,
    ExpresionEnlazada Lista,
    BloqueEnlazado Cuerpo) : SentenciaEnlazada;

/// <summary><c>coleccion[indice] = valor;</c>.</summary>
public sealed record AsignacionIndiceEnlazada(ExpresionEnlazada Objeto, ExpresionEnlazada Indice, ExpresionEnlazada Valor) : SentenciaEnlazada;

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

/// <summary><c>[1, 2, 3]</c>, ya con el tipo de la lista y los elementos convertidos a su tipo de elemento.</summary>
public sealed record ListaLiteralEnlazado(TipoGenerico TipoLista, ImmutableArray<ExpresionEnlazada> Elementos) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoLista;
}

public sealed record ParEnlazado(ExpresionEnlazada Clave, ExpresionEnlazada Valor) : NodoEnlazado;

/// <summary><c>{"a": 1}</c>, ya con el tipo del mapa y las claves y valores convertidos.</summary>
public sealed record MapaLiteralEnlazado(TipoGenerico TipoMapa, ImmutableArray<ParEnlazado> Pares) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoMapa;
}

/// <summary><c>nuevo lista&lt;T&gt;()</c> o <c>nuevo mapa&lt;K, V&gt;()</c>: una colección vacía.</summary>
public sealed record NuevaColeccionEnlazada(TipoGenerico TipoColeccion) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoColeccion;
}

/// <summary><c>coleccion[indice]</c>. El índice ya viene convertido (entero en listas, la clave en mapas).</summary>
public sealed record IndiceEnlazado(ExpresionEnlazada Objeto, ExpresionEnlazada Indice, TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

/// <summary>Un campo de solo lectura o un método de un tipo integrado (<c>texto.longitud</c>, <c>lista.agregar(x)</c>).</summary>
public sealed record MiembroIntegradoEnlazado(ExpresionEnlazada Objeto, MiembroIntegrado Miembro, ImmutableArray<ExpresionEnlazada> Argumentos) : ExpresionEnlazada
{
    public override TipoLince Tipo => Miembro.TipoRetorno;
}

/// <summary>Una lambda ya enlazada: su firma como símbolo, su cuerpo (siempre un bloque) y su tipo función.</summary>
/// <param name="DelegadoClr">Cuando va a un parámetro de .NET con un delegado propio (<c>Predicate&lt;T&gt;</c>), ese tipo; null para Func/Action.</param>
public sealed record LambdaEnlazada(SimboloFuncion Simbolo, BloqueEnlazado Cuerpo, TipoFuncion TipoFuncion, Type? DelegadoClr = null) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoFuncion;
}

/// <summary>Una función libre usada como valor: <c>numeros.transformar(doble)</c>.</summary>
public sealed record ReferenciaFuncionEnlazada(SimboloFuncion Funcion, TipoFuncion TipoFuncion) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoFuncion;
}

/// <summary>Llamada a un valor de tipo función: <c>f(1, 2)</c> donde <c>f</c> es una variable, parámetro o campo.</summary>
public sealed record InvocacionEnlazada(ExpresionEnlazada Funcion, ImmutableArray<ExpresionEnlazada> Argumentos) : ExpresionEnlazada
{
    public override TipoLince Tipo => ((TipoFuncion)Funcion.Tipo).Retorno;
}

// ----- Interoperabilidad con .NET -----

/// <summary>Un tipo de .NET nombrado como receptor de miembros estáticos (<c>Math</c> en <c>Math.Sqrt(x)</c>). No es un valor.</summary>
public sealed record TipoComoReceptorEnlazado(TipoExterno TipoExterno) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoLince.NombreDeTipo;
}

/// <summary>
/// Llamada a un método de .NET, de instancia (con receptor) o estático. Los argumentos
/// llegan con tipos de Lince; el emisor los ajusta a los parámetros de .NET en la frontera
/// (<c>entero</c> a <c>int</c>, empaquetado a <c>object</c>) y ajusta el resultado al revés.
/// </summary>
public sealed record LlamadaExternaEnlazada(
    ExpresionEnlazada? Receptor,
    System.Reflection.MethodInfo Metodo,
    ImmutableArray<ExpresionEnlazada> Argumentos,
    TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

/// <summary><c>nuevo StringBuilder(...)</c>: un constructor de .NET.</summary>
public sealed record NuevoExternoEnlazado(
    System.Reflection.ConstructorInfo Constructor,
    ImmutableArray<ExpresionEnlazada> Argumentos,
    TipoExterno TipoExterno) : ExpresionEnlazada
{
    public override TipoLince Tipo => TipoExterno;
}

/// <summary>Lectura de una propiedad o campo de .NET, de instancia o estático (<c>DateTime.Now</c>, <c>sb.Length</c>, <c>DayOfWeek.Friday</c>).</summary>
public sealed record MiembroExternoEnlazado(ExpresionEnlazada? Receptor, System.Reflection.MemberInfo Miembro, TipoLince Tipo) : ExpresionEnlazada
{
    public override TipoLince Tipo { get; } = Tipo;
}

/// <summary>Escritura de una propiedad o campo de .NET: <c>objeto.Propiedad = valor;</c>.</summary>
public sealed record AsignacionExternaEnlazada(ExpresionEnlazada? Receptor, System.Reflection.MemberInfo Miembro, ExpresionEnlazada Valor) : SentenciaEnlazada;

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
