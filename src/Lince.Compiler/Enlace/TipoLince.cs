using Lince.Compiler.Sintaxis;

namespace Lince.Compiler.Enlace;

/// <summary>
/// Un tipo del lenguaje y el tipo de .NET con el que se emite. En esta fase los tipos son
/// fijos; cuando lleguen las estructuras y clases, esta clase pasará a ser la base de una
/// jerarquía.
/// </summary>
public sealed class TipoLince
{
    public static readonly TipoLince Entero = new("entero", typeof(long));
    public static readonly TipoLince Decimal = new("decimal", typeof(double));
    public static readonly TipoLince Texto = new("texto", typeof(string));
    public static readonly TipoLince Logico = new("logico", typeof(bool));
    public static readonly TipoLince Nada = new("nada", typeof(void));

    /// <summary>El tipo del literal <c>nulo</c> antes de convertirse a <c>texto</c>.</summary>
    public static readonly TipoLince Nulo = new("nulo", typeof(object));

    /// <summary>
    /// El tipo de una expresión que ya tiene un error. Es compatible con todo para que un
    /// solo error no se multiplique en cascada.
    /// </summary>
    public static readonly TipoLince Error = new("?", typeof(object));

    private TipoLince(string nombre, Type tipoClr)
    {
        Nombre = nombre;
        TipoClr = tipoClr;
    }

    public string Nombre { get; }

    public Type TipoClr { get; }

    public bool EsError => ReferenceEquals(this, Error);

    public bool EsNumerico => ReferenceEquals(this, Entero) || ReferenceEquals(this, Decimal);

    /// <summary>Tipos con los que se puede concatenar un texto o imprimir.</summary>
    public bool SePuedeEscribir => ReferenceEquals(this, Entero) || ReferenceEquals(this, Decimal)
        || ReferenceEquals(this, Texto) || ReferenceEquals(this, Logico) || ReferenceEquals(this, Nulo);

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
