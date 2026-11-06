using Lince.Compiler.Texto;

namespace Lince.Compiler.Sintaxis;

/// <summary>
/// Un token del archivo fuente. <see cref="Valor"/> lleva el literal ya convertido
/// (<c>long</c>, <c>double</c> o <c>string</c>) cuando aplica.
/// </summary>
/// <param name="EsFaltante">
/// Verdadero cuando el analizador sintáctico lo inventó para recuperarse de un error: el
/// token esperado no estaba. Las fases siguientes no informan errores sobre estos tokens.
/// </param>
public sealed record Token(TipoToken Tipo, string Texto, object? Valor, Ubicacion Ubicacion, bool EsFaltante = false)
{
    public int Linea => Ubicacion.Linea;

    public int Columna => Ubicacion.Columna;

    public override string ToString() => $"{Tipo} '{Texto}' en {Ubicacion}";
}
