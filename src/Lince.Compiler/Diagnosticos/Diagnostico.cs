using Lince.Compiler.Texto;

namespace Lince.Compiler.Diagnosticos;

public enum Severidad
{
    Error,
    Advertencia,
}

/// <summary>
/// Un mensaje del compilador con su código y su ubicación. Se imprime con el formato que
/// entienden los editores y el MSBuild: <c>ruta(linea,columna): error LN1234: mensaje</c>.
/// </summary>
public sealed record Diagnostico(string Codigo, Severidad Severidad, Ubicacion Ubicacion, string Mensaje)
{
    public int Linea => Ubicacion.Linea;

    public int Columna => Ubicacion.Columna;

    public bool EsError => Severidad == Severidad.Error;

    public override string ToString()
    {
        var severidad = Severidad == Severidad.Error ? "error" : "advertencia";
        return $"{Ubicacion.Archivo.Ruta}({Linea},{Columna}): {severidad} {Codigo}: {Mensaje}";
    }
}
