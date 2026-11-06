using System.Collections.Frozen;

namespace Lince.Compiler.Sintaxis;

/// <summary>
/// Tabla de palabras clave y descripciones de tokens para los mensajes. Es el único sitio
/// donde se escribe cada palabra del lenguaje; la especificación (docs/especificacion.md)
/// debe coincidir con esta tabla.
/// </summary>
public static class Palabras
{
    private static readonly FrozenDictionary<string, TipoToken> _palabrasClave = new Dictionary<string, TipoToken>
    {
        ["funcion"] = TipoToken.PalabraFuncion,
        ["sea"] = TipoToken.PalabraSea,
        ["var"] = TipoToken.PalabraVar,
        ["si"] = TipoToken.PalabraSi,
        ["sino"] = TipoToken.PalabraSino,
        ["mientras"] = TipoToken.PalabraMientras,
        ["para"] = TipoToken.PalabraPara,
        ["en"] = TipoToken.PalabraEn,
        ["regresar"] = TipoToken.PalabraRegresar,
        ["romper"] = TipoToken.PalabraRomper,
        ["continuar"] = TipoToken.PalabraContinuar,
        ["verdadero"] = TipoToken.PalabraVerdadero,
        ["falso"] = TipoToken.PalabraFalso,
        ["nulo"] = TipoToken.PalabraNulo,
        ["entero"] = TipoToken.PalabraEntero,
        ["decimal"] = TipoToken.PalabraDecimal,
        ["texto"] = TipoToken.PalabraTexto,
        ["logico"] = TipoToken.PalabraLogico,
        ["nada"] = TipoToken.PalabraNada,
        ["clase"] = TipoToken.PalabraClase,
        ["estructura"] = TipoToken.PalabraEstructura,
        ["nuevo"] = TipoToken.PalabraNuevo,
        ["este"] = TipoToken.PalabraEste,
        ["privado"] = TipoToken.PalabraPrivado,
        ["publico"] = TipoToken.PalabraPublico,
    }.ToFrozenDictionary();

    /// <summary>
    /// Reservadas para las fases de la hoja de ruta. Se reservan desde ya para que los programas
    /// de hoy no dejen de compilar cuando lleguen.
    /// </summary>
    private static readonly FrozenSet<string> _reservadas = new[]
    {
        "modulo", "importar", "segun", "caso", "intentar", "capturar", "lanzar", "lista", "mapa",
        "interfaz", "enumeracion", "hereda", "asincrono", "esperar",
    }.ToFrozenSet();

    public static TipoToken TipoDePalabra(string texto)
    {
        if (_palabrasClave.TryGetValue(texto, out var tipo))
        {
            return tipo;
        }

        return _reservadas.Contains(texto) ? TipoToken.PalabraReservada : TipoToken.Identificador;
    }

    public static bool EsTipo(TipoToken tipo) => tipo is TipoToken.PalabraEntero or TipoToken.PalabraDecimal
        or TipoToken.PalabraTexto or TipoToken.PalabraLogico or TipoToken.PalabraNada;

    /// <summary>Texto fijo de un token, para sintetizarlo o describirlo en un mensaje.</summary>
    public static string TextoFijo(TipoToken tipo) => tipo switch
    {
        TipoToken.ParentesisAbre => "(",
        TipoToken.ParentesisCierra => ")",
        TipoToken.LlaveAbre => "{",
        TipoToken.LlaveCierra => "}",
        TipoToken.Coma => ",",
        TipoToken.PuntoYComa => ";",
        TipoToken.DosPuntos => ":",
        TipoToken.Punto => ".",
        TipoToken.PuntoPunto => "..",
        TipoToken.Mas => "+",
        TipoToken.Menos => "-",
        TipoToken.Asterisco => "*",
        TipoToken.Barra => "/",
        TipoToken.Porcentaje => "%",
        TipoToken.Igual => "=",
        TipoToken.MasIgual => "+=",
        TipoToken.MenosIgual => "-=",
        TipoToken.AsteriscoIgual => "*=",
        TipoToken.BarraIgual => "/=",
        TipoToken.PorcentajeIgual => "%=",
        TipoToken.IgualIgual => "==",
        TipoToken.Distinto => "!=",
        TipoToken.Menor => "<",
        TipoToken.MenorIgual => "<=",
        TipoToken.Mayor => ">",
        TipoToken.MayorIgual => ">=",
        TipoToken.YLogico => "&&",
        TipoToken.OLogico => "||",
        TipoToken.Negacion => "!",
        _ => _palabrasClave.FirstOrDefault(p => p.Value == tipo).Key ?? "",
    };

    /// <summary>Cómo se nombra un tipo de token en un mensaje de error.</summary>
    public static string Describir(TipoToken tipo) => tipo switch
    {
        TipoToken.FinDeArchivo => "el fin del archivo",
        TipoToken.Invalido => "un carácter no válido",
        TipoToken.Identificador => "un identificador",
        TipoToken.LiteralEntero => "un número entero",
        TipoToken.LiteralDecimal => "un número decimal",
        TipoToken.LiteralTexto => "un texto",
        TipoToken.PalabraReservada => "una palabra reservada",
        _ => $"'{TextoFijo(tipo)}'",
    };
}
