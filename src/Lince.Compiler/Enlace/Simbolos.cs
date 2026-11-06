using System.Collections.Immutable;
using Lince.Compiler.Sintaxis;

namespace Lince.Compiler.Enlace;

public abstract class Simbolo
{
    protected Simbolo(string nombre)
    {
        Nombre = nombre;
    }

    public string Nombre { get; }

    public override string ToString() => Nombre;
}

public enum ClaseDeVariable
{
    Local,
    Parametro,

    /// <summary>La variable de un <c>para</c>: inmutable para el programa, la avanza el ciclo.</summary>
    Ciclo,

    /// <summary>Una temporal que crea el compilador (el límite de un <c>para</c>); no tiene nombre visible.</summary>
    Oculta,
}

public sealed class SimboloVariable : Simbolo
{
    public SimboloVariable(string nombre, TipoLince tipo, bool esMutable, ClaseDeVariable clase, int indiceDeParametro = -1)
        : base(nombre)
    {
        Tipo = tipo;
        EsMutable = esMutable;
        Clase = clase;
        IndiceDeParametro = indiceDeParametro;
    }

    public TipoLince Tipo { get; }

    public bool EsMutable { get; }

    public ClaseDeVariable Clase { get; }

    /// <summary>Posición del parámetro en la firma, o -1 si no es parámetro.</summary>
    public int IndiceDeParametro { get; }
}

public sealed class SimboloFuncion : Simbolo
{
    public SimboloFuncion(string nombre, ImmutableArray<SimboloVariable> parametros, TipoLince tipoRetorno, FuncionSintaxis? declaracion)
        : base(nombre)
    {
        Parametros = parametros;
        TipoRetorno = tipoRetorno;
        Declaracion = declaracion;
    }

    public ImmutableArray<SimboloVariable> Parametros { get; }

    public TipoLince TipoRetorno { get; }

    public FuncionSintaxis? Declaracion { get; }
}
