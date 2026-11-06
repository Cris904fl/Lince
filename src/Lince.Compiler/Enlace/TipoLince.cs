using System.Collections.Immutable;
using Lince.Compiler.Sintaxis;

namespace Lince.Compiler.Enlace;

/// <summary>
/// Un tipo del lenguaje. Los básicos son instancias fijas con su tipo de .NET; los declarados
/// por el programa son <see cref="TipoUsuario"/> y su tipo de .NET lo crea el emisor.
/// </summary>
public class TipoLince
{
    public static readonly TipoLince Entero = new("entero", typeof(long));
    public static readonly TipoLince Decimal = new("decimal", typeof(double));
    public static readonly TipoLince Texto = new("texto", typeof(string));
    public static readonly TipoLince Logico = new("logico", typeof(bool));
    public static readonly TipoLince Nada = new("nada", typeof(void));

    /// <summary>El tipo del literal <c>nulo</c> antes de convertirse a un tipo por referencia.</summary>
    public static readonly TipoLince Nulo = new("nulo", typeof(object));

    /// <summary>
    /// El tipo de una expresión que ya tiene un error. Es compatible con todo para que un
    /// solo error no se multiplique en cascada.
    /// </summary>
    public static readonly TipoLince Error = new("?", typeof(object));

    private readonly Type? _tipoClr;

    protected TipoLince(string nombre, Type? tipoClr)
    {
        Nombre = nombre;
        _tipoClr = tipoClr;
    }

    public string Nombre { get; }

    /// <summary>El tipo de .NET de un tipo básico. Los tipos de usuario los resuelve el emisor.</summary>
    public Type TipoClr => _tipoClr ?? throw new InvalidOperationException($"El tipo '{Nombre}' no tiene un tipo de .NET fijo.");

    public bool EsError => ReferenceEquals(this, Error);

    public bool EsNumerico => ReferenceEquals(this, Entero) || ReferenceEquals(this, Decimal);

    /// <summary>Tipos con los que se puede concatenar un texto o imprimir. Un <c>texto?</c> también: nulo se escribe vacío.</summary>
    public bool SePuedeEscribir => ReferenceEquals(this, Entero) || ReferenceEquals(this, Decimal)
        || ReferenceEquals(this, Texto) || ReferenceEquals(this, Logico) || ReferenceEquals(this, Nulo)
        || this is TipoNulable { Base: var b } && ReferenceEquals(b, Texto);

    /// <summary>Tipos por referencia: se pueden hacer nulables y se comparan por identidad.</summary>
    public bool EsReferencia => ReferenceEquals(this, Texto) || this is TipoUsuario { EsEstructura: false } || this is TipoNulable;

    public bool EsEstructura => this is TipoUsuario { EsEstructura: true };

    public bool EsNulable => this is TipoNulable;

    /// <summary>El tipo sin el <c>?</c>: <c>texto</c> para <c>texto?</c>; el mismo si no es nulable.</summary>
    public TipoLince SinNulo => this is TipoNulable nulable ? nulable.Base : this;

    /// <summary>Verdadero si es <c>texto</c> o <c>texto?</c>.</summary>
    public bool EsTextual => ReferenceEquals(SinNulo, Texto);

    private static readonly Dictionary<TipoLince, TipoNulable> _nulables = new();

    /// <summary>El tipo <c>T?</c> de un tipo por referencia, único por tipo base para poder comparar por referencia.</summary>
    public static TipoNulable Nulable(TipoLince baseTipo)
    {
        lock (_nulables)
        {
            if (!_nulables.TryGetValue(baseTipo, out var nulable))
            {
                nulable = new TipoNulable(baseTipo);
                _nulables[baseTipo] = nulable;
            }

            return nulable;
        }
    }

    public static TipoLince? DesdePalabra(TipoToken tipo) => tipo switch
    {
        TipoToken.PalabraEntero => Entero,
        TipoToken.PalabraDecimal => Decimal,
        TipoToken.PalabraTexto => Texto,
        TipoToken.PalabraLogico => Logico,
        TipoToken.PalabraNada => Nada,
        _ => null,
    };

    public override string ToString() => Nombre;
}

/// <summary>
/// <c>T?</c>: un tipo por referencia que además puede ser <c>nulo</c>. En .NET es el mismo
/// tipo que su base; la diferencia la vigila el enlazador con el análisis de flujo.
/// </summary>
public sealed class TipoNulable : TipoLince
{
    internal TipoNulable(TipoLince baseTipo)
        : base(baseTipo.Nombre + "?", null)
    {
        Base = baseTipo;
    }

    public TipoLince Base { get; }
}

/// <summary>
/// Una clase o estructura declarada en el programa. Sus miembros se rellenan en la fase de
/// declaración del enlazador, antes de enlazar ningún cuerpo, para que cualquier función
/// pueda usar cualquier tipo sin importar el orden del archivo.
/// </summary>
public sealed class TipoUsuario : TipoLince
{
    private readonly List<SimboloCampo> _campos = [];
    private readonly Dictionary<string, Simbolo> _miembros = new(StringComparer.Ordinal);

    public TipoUsuario(string nombre, TipoDeclaradoSintaxis declaracion)
        : base(nombre, null)
    {
        Declaracion = declaracion;
        EsEstructura = declaracion.EsEstructura;
    }

    public TipoDeclaradoSintaxis Declaracion { get; }

    public new bool EsEstructura { get; }

    public string Clase => EsEstructura ? "estructura" : "clase";

    /// <summary>Campos en orden de declaración; el orden importa para el constructor implícito.</summary>
    public ImmutableArray<SimboloCampo> Campos => [.. _campos];

    public IEnumerable<SimboloFuncion> Metodos => _miembros.Values.OfType<SimboloFuncion>().Where(m => !m.EsConstructor);

    /// <summary>El constructor escrito con <c>funcion nuevo(...)</c>, si lo hay.</summary>
    public SimboloFuncion? ConstructorExplicito { get; internal set; }

    /// <summary>
    /// Parámetros de <c>nuevo Tipo(...)</c>: los del constructor explícito, o uno por cada
    /// campo sin valor inicial, en orden, si no hay constructor.
    /// </summary>
    public ImmutableArray<SimboloVariable> ParametrosDeNuevo => ConstructorExplicito?.Parametros
        ?? [.. _campos.Where(c => c.Declaracion.Inicial is null)
            .Select((c, i) => new SimboloVariable(c.Nombre, c.Tipo, esMutable: false, ClaseDeVariable.Parametro, i))];

    public Simbolo? BuscarMiembro(string nombre) => _miembros.GetValueOrDefault(nombre);

    public SimboloCampo? BuscarCampo(string nombre) => BuscarMiembro(nombre) as SimboloCampo;

    public SimboloFuncion? BuscarMetodo(string nombre) => BuscarMiembro(nombre) as SimboloFuncion;

    internal bool AgregarCampo(SimboloCampo campo)
    {
        if (!_miembros.TryAdd(campo.Nombre, campo))
        {
            return false;
        }

        _campos.Add(campo);
        return true;
    }

    internal bool AgregarMetodo(SimboloFuncion metodo) => _miembros.TryAdd(metodo.Nombre, metodo);
}
