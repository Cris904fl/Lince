using System.Collections.Immutable;

namespace Lince.Compiler.Texto;

/// <summary>
/// Un archivo fuente .ln ya leído: su ruta, su contenido y la tabla de inicios de línea
/// con la que una posición absoluta se traduce a línea y columna para los diagnósticos.
/// </summary>
public sealed class ArchivoFuente
{
    private readonly ImmutableArray<int> _iniciosDeLinea;

    public ArchivoFuente(string ruta, string contenido)
    {
        Ruta = ruta;
        Contenido = contenido;
        _iniciosDeLinea = CalcularIniciosDeLinea(contenido);
    }

    /// <summary>Ruta tal como la dio el usuario; se muestra en los diagnósticos.</summary>
    public string Ruta { get; }

    public string Contenido { get; }

    public int Longitud => Contenido.Length;

    public int CantidadDeLineas => _iniciosDeLinea.Length;

    public char this[int indice] => Contenido[indice];

    public static ArchivoFuente Leer(string ruta) => new(ruta, File.ReadAllText(ruta));

    /// <summary>Para pruebas y para compilar texto que no viene de disco.</summary>
    public static ArchivoFuente DesdeTexto(string contenido, string ruta = "<memoria>") => new(ruta, contenido);

    /// <summary>Línea y columna (ambas desde 1) de una posición absoluta en el contenido.</summary>
    public (int Linea, int Columna) Posicion(int indice)
    {
        var linea = BuscarLinea(indice);
        var columna = indice - _iniciosDeLinea[linea] + 1;
        return (linea + 1, columna);
    }

    /// <summary>Texto de una línea (desde 1), sin el salto de línea final.</summary>
    public string TextoDeLinea(int linea)
    {
        var inicio = _iniciosDeLinea[linea - 1];
        var fin = linea < _iniciosDeLinea.Length ? _iniciosDeLinea[linea] : Contenido.Length;
        return Contenido[inicio..fin].TrimEnd('\r', '\n');
    }

    private int BuscarLinea(int indice)
    {
        // Búsqueda binaria del último inicio de línea menor o igual que el índice.
        int bajo = 0, alto = _iniciosDeLinea.Length - 1;
        while (bajo < alto)
        {
            var medio = (bajo + alto + 1) / 2;
            if (_iniciosDeLinea[medio] <= indice)
            {
                bajo = medio;
            }
            else
            {
                alto = medio - 1;
            }
        }

        return bajo;
    }

    private static ImmutableArray<int> CalcularIniciosDeLinea(string contenido)
    {
        var inicios = ImmutableArray.CreateBuilder<int>();
        inicios.Add(0);
        for (var i = 0; i < contenido.Length; i++)
        {
            if (contenido[i] == '\n')
            {
                inicios.Add(i + 1);
            }
            else if (contenido[i] == '\r' && (i + 1 >= contenido.Length || contenido[i + 1] != '\n'))
            {
                inicios.Add(i + 1);
            }
        }

        return inicios.ToImmutable();
    }
}
