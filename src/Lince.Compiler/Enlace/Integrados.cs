using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;

namespace Lince.Compiler.Enlace;

/// <summary>Qué se añade a la llamada antes del método de .NET, cuando la firma de .NET pide más que la de Lince.</summary>
public enum ArgumentoExtra
{
    Ninguno,

    /// <summary><c>CultureInfo.InvariantCulture</c>: números siempre con punto, sin depender del idioma del equipo.</summary>
    CulturaInvariante,

    /// <summary><c>StringComparison.Ordinal</c>: comparar textos por sus caracteres, no por reglas de idioma.</summary>
    ComparacionOrdinal,

    /// <summary><c>StringSplitOptions.None</c>.</summary>
    OpcionesDeSplitNinguna,
}

/// <summary>Cómo se ajusta el resultado de .NET al tipo de Lince.</summary>
public enum Posproceso
{
    Ninguno,

    /// <summary>Un <c>int</c> de .NET pasa a <c>entero</c> (64 bits).</summary>
    AEntero64,

    /// <summary>Un <c>entero</c> pasa a <c>decimal</c> (<c>conv.r8</c>), sin llamar a nada.</summary>
    ADecimal,

    /// <summary>Un <c>decimal</c> pasa a <c>entero</c> truncando (<c>conv.i8</c>), sin llamar a nada.</summary>
    AEnteroTruncando,

    /// <summary>Un arreglo o colección de .NET se copia a una <c>lista</c> con <c>new List&lt;T&gt;(IEnumerable&lt;T&gt;)</c>.</summary>
    ALista,
}

/// <summary>
/// Cómo se emite un miembro integrado: el método de .NET (sobre el tipo genérico abierto si
/// el receptor es una colección) y los ajustes alrededor de la llamada.
/// </summary>
/// <param name="Metodo">Null cuando no hay llamada y solo se aplica el posproceso (una conversión).</param>
/// <param name="EsEstatico">El receptor se pasa como primer argumento (<c>Math.Abs(x)</c>, <c>long.Parse(s)</c>).</param>
/// <param name="ArgumentosAInt32">Qué argumentos de Lince (<c>entero</c>) se convierten a <c>int</c> antes de llamar.</param>
public sealed record RecetaIl(
    MethodInfo? Metodo,
    bool EsEstatico = false,
    ImmutableArray<bool> ArgumentosAInt32 = default,
    ArgumentoExtra Extra = ArgumentoExtra.Ninguno,
    Posproceso Posproceso = Posproceso.Ninguno)
{
    public bool ArgumentoAInt32(int indice) => !ArgumentosAInt32.IsDefault && indice < ArgumentosAInt32.Length && ArgumentosAInt32[indice];
}

/// <summary>Un campo de solo lectura o un método que el lenguaje ofrece sobre un tipo integrado.</summary>
public sealed record MiembroIntegrado(
    string Nombre,
    bool EsPropiedad,
    ImmutableArray<SimboloVariable> Parametros,
    TipoLince TipoRetorno,
    RecetaIl Receta);

/// <summary>
/// La tabla de miembros de <c>texto</c>, <c>entero</c>, <c>decimal</c>, <c>lista&lt;T&gt;</c> y
/// <c>mapa&lt;K, V&gt;</c>. Son fachadas finas sobre <c>String</c>, <c>Math</c>, <c>List&lt;T&gt;</c>
/// y <c>Dictionary&lt;K, V&gt;</c>: nombrar en español lo que .NET ya hace.
/// </summary>
public static class Integrados
{
    private static readonly Type ListaAbierta = typeof(List<>);
    private static readonly Type MapaAbierto = typeof(Dictionary<,>);
    private static readonly Type T = ListaAbierta.GetGenericArguments()[0];
    private static readonly Type K = MapaAbierto.GetGenericArguments()[0];

    private static readonly Dictionary<TipoLince, ImmutableArray<MiembroIntegrado>> _cache = new();

    public static MiembroIntegrado? Buscar(TipoLince tipo, string nombre) =>
        MiembrosDe(tipo).FirstOrDefault(m => m.Nombre == nombre);

    public static ImmutableArray<MiembroIntegrado> MiembrosDe(TipoLince tipo)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(tipo, out var miembros))
            {
                miembros = Construir(tipo);
                _cache[tipo] = miembros;
            }

            return miembros;
        }
    }

    private static ImmutableArray<MiembroIntegrado> Construir(TipoLince tipo)
    {
        if (ReferenceEquals(tipo, TipoLince.Texto))
        {
            return DeTexto();
        }

        if (ReferenceEquals(tipo, TipoLince.Entero))
        {
            return DeEntero();
        }

        if (ReferenceEquals(tipo, TipoLince.Decimal))
        {
            return DeDecimal();
        }

        return tipo switch
        {
            TipoGenerico { EsLista: true } lista => DeLista(lista),
            TipoGenerico { EsMapa: true } mapa => DeMapa(mapa),
            _ => [],
        };
    }

    // ----- texto -----

    private static ImmutableArray<MiembroIntegrado> DeTexto()
    {
        var texto = TipoLince.Texto;
        var entero = TipoLince.Entero;
        return
        [
            Propiedad("longitud", entero, new RecetaIl(Propiedad(typeof(string), nameof(string.Length)), Posproceso: Posproceso.AEntero64)),
            Metodo("mayusculas", [], texto, new RecetaIl(De(typeof(string), nameof(string.ToUpperInvariant)))),
            Metodo("minusculas", [], texto, new RecetaIl(De(typeof(string), nameof(string.ToLowerInvariant)))),
            Metodo("recortar", [], texto, new RecetaIl(De(typeof(string), nameof(string.Trim)))),
            Metodo("contiene", [("otro", texto)], TipoLince.Logico, new RecetaIl(De(typeof(string), nameof(string.Contains), typeof(string)))),
            Metodo("empiezaCon", [("otro", texto)], TipoLince.Logico,
                new RecetaIl(De(typeof(string), nameof(string.StartsWith), typeof(string), typeof(StringComparison)), Extra: ArgumentoExtra.ComparacionOrdinal)),
            Metodo("terminaCon", [("otro", texto)], TipoLince.Logico,
                new RecetaIl(De(typeof(string), nameof(string.EndsWith), typeof(string), typeof(StringComparison)), Extra: ArgumentoExtra.ComparacionOrdinal)),
            Metodo("indiceDe", [("otro", texto)], entero,
                new RecetaIl(De(typeof(string), nameof(string.IndexOf), typeof(string), typeof(StringComparison)), Extra: ArgumentoExtra.ComparacionOrdinal, Posproceso: Posproceso.AEntero64)),
            Metodo("reemplazar", [("buscado", texto), ("reemplazo", texto)], texto,
                new RecetaIl(De(typeof(string), nameof(string.Replace), typeof(string), typeof(string)))),
            Metodo("subtexto", [("inicio", entero), ("longitud", entero)], texto,
                new RecetaIl(De(typeof(string), nameof(string.Substring), typeof(int), typeof(int)), ArgumentosAInt32: [true, true])),
            Metodo("partir", [("separador", texto)], TipoLince.Lista(texto),
                new RecetaIl(De(typeof(string), nameof(string.Split), typeof(string), typeof(StringSplitOptions)), Extra: ArgumentoExtra.OpcionesDeSplitNinguna, Posproceso: Posproceso.ALista)),
            Metodo("aEntero", [], entero,
                new RecetaIl(De(typeof(long), nameof(long.Parse), typeof(string), typeof(IFormatProvider)), EsEstatico: true, Extra: ArgumentoExtra.CulturaInvariante)),
            Metodo("aDecimal", [], TipoLince.Decimal,
                new RecetaIl(De(typeof(double), nameof(double.Parse), typeof(string), typeof(IFormatProvider)), EsEstatico: true, Extra: ArgumentoExtra.CulturaInvariante)),
        ];
    }

    // ----- números -----

    private static ImmutableArray<MiembroIntegrado> DeEntero() =>
    [
        Metodo("absoluto", [], TipoLince.Entero, new RecetaIl(De(typeof(Math), nameof(Math.Abs), typeof(long)), EsEstatico: true)),
        Metodo("aDecimal", [], TipoLince.Decimal, new RecetaIl(null, Posproceso: Posproceso.ADecimal)),
    ];

    private static ImmutableArray<MiembroIntegrado> DeDecimal()
    {
        var d = TipoLince.Decimal;
        return
        [
            Metodo("absoluto", [], d, new RecetaIl(De(typeof(Math), nameof(Math.Abs), typeof(double)), EsEstatico: true)),
            Metodo("redondear", [], d, new RecetaIl(De(typeof(Math), nameof(Math.Round), typeof(double)), EsEstatico: true)),
            Metodo("piso", [], d, new RecetaIl(De(typeof(Math), nameof(Math.Floor), typeof(double)), EsEstatico: true)),
            Metodo("techo", [], d, new RecetaIl(De(typeof(Math), nameof(Math.Ceiling), typeof(double)), EsEstatico: true)),
            Metodo("raiz", [], d, new RecetaIl(De(typeof(Math), nameof(Math.Sqrt), typeof(double)), EsEstatico: true)),
            Metodo("potencia", [("exponente", d)], d, new RecetaIl(De(typeof(Math), nameof(Math.Pow), typeof(double), typeof(double)), EsEstatico: true)),
            Metodo("aEntero", [], TipoLince.Entero, new RecetaIl(null, Posproceso: Posproceso.AEnteroTruncando)),
        ];
    }

    // ----- lista<T> -----

    private static ImmutableArray<MiembroIntegrado> DeLista(TipoGenerico lista)
    {
        var elemento = lista.Elemento;
        var entero = TipoLince.Entero;
        return
        [
            Propiedad("cantidad", entero, new RecetaIl(Propiedad(ListaAbierta, nameof(List<int>.Count)), Posproceso: Posproceso.AEntero64)),
            Metodo("agregar", [("elemento", elemento)], TipoLince.Nada, new RecetaIl(De(ListaAbierta, nameof(List<int>.Add), T))),
            Metodo("insertar", [("indice", entero), ("elemento", elemento)], TipoLince.Nada,
                new RecetaIl(De(ListaAbierta, nameof(List<int>.Insert), typeof(int), T), ArgumentosAInt32: [true, false])),
            Metodo("quitarEn", [("indice", entero)], TipoLince.Nada,
                new RecetaIl(De(ListaAbierta, nameof(List<int>.RemoveAt), typeof(int)), ArgumentosAInt32: [true])),
            Metodo("quitar", [("elemento", elemento)], TipoLince.Logico, new RecetaIl(De(ListaAbierta, nameof(List<int>.Remove), T))),
            Metodo("contiene", [("elemento", elemento)], TipoLince.Logico, new RecetaIl(De(ListaAbierta, nameof(List<int>.Contains), T))),
            Metodo("indiceDe", [("elemento", elemento)], entero,
                new RecetaIl(De(ListaAbierta, nameof(List<int>.IndexOf), T), Posproceso: Posproceso.AEntero64)),
            Metodo("limpiar", [], TipoLince.Nada, new RecetaIl(De(ListaAbierta, nameof(List<int>.Clear)))),
        ];
    }

    // ----- mapa<K, V> -----

    private static ImmutableArray<MiembroIntegrado> DeMapa(TipoGenerico mapa)
    {
        var clave = mapa.Argumentos[0];
        var valor = mapa.Argumentos[1];
        return
        [
            Propiedad("cantidad", TipoLince.Entero, new RecetaIl(Propiedad(MapaAbierto, nameof(Dictionary<int, int>.Count)), Posproceso: Posproceso.AEntero64)),
            Metodo("contiene", [("clave", clave)], TipoLince.Logico, new RecetaIl(De(MapaAbierto, nameof(Dictionary<int, int>.ContainsKey), K))),
            Metodo("quitar", [("clave", clave)], TipoLince.Logico, new RecetaIl(De(MapaAbierto, nameof(Dictionary<int, int>.Remove), K))),
            Metodo("limpiar", [], TipoLince.Nada, new RecetaIl(De(MapaAbierto, nameof(Dictionary<int, int>.Clear)))),
            Metodo("claves", [], TipoLince.Lista(clave), new RecetaIl(Propiedad(MapaAbierto, nameof(Dictionary<int, int>.Keys)), Posproceso: Posproceso.ALista)),
            Metodo("valores", [], TipoLince.Lista(valor), new RecetaIl(Propiedad(MapaAbierto, nameof(Dictionary<int, int>.Values)), Posproceso: Posproceso.ALista)),
        ];
    }

    // ----- Ayudas -----

    private static MiembroIntegrado Propiedad(string nombre, TipoLince tipo, RecetaIl receta) =>
        new(nombre, EsPropiedad: true, [], tipo, receta);

    private static MiembroIntegrado Metodo(string nombre, (string Nombre, TipoLince Tipo)[] parametros, TipoLince retorno, RecetaIl receta) =>
        new(nombre, EsPropiedad: false,
            [.. parametros.Select((p, i) => new SimboloVariable(p.Nombre, p.Tipo, esMutable: false, ClaseDeVariable.Parametro, i))],
            retorno, receta);

    private static MethodInfo De(Type tipo, string nombre, params Type[] parametros) =>
        tipo.GetMethod(nombre, parametros) ?? throw new MissingMethodException(tipo.Name, nombre);

    private static MethodInfo Propiedad(Type tipo, string nombre) =>
        tipo.GetProperty(nombre)?.GetMethod ?? throw new MissingMemberException(tipo.Name, nombre);
}
