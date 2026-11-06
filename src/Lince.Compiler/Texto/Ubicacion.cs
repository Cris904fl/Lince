namespace Lince.Compiler.Texto;

/// <summary>
/// Un tramo de un archivo fuente: posición absoluta de inicio y longitud. Línea y columna se
/// calculan al pedirlas, así los tokens y nodos solo guardan dos enteros.
/// </summary>
public readonly record struct Ubicacion(ArchivoFuente Archivo, int Inicio, int Longitud)
{
    public int Fin => Inicio + Longitud;

    public int Linea => Archivo.Posicion(Inicio).Linea;

    public int Columna => Archivo.Posicion(Inicio).Columna;

    public string Texto => Archivo.Contenido.Substring(Inicio, Longitud);

    /// <summary>La ubicación que va desde el inicio de la primera hasta el fin de la segunda.</summary>
    public static Ubicacion Entre(Ubicacion primera, Ubicacion ultima) =>
        new(primera.Archivo, primera.Inicio, Math.Max(0, ultima.Fin - primera.Inicio));

    public override string ToString() => $"{Archivo.Ruta}({Linea},{Columna})";
}
