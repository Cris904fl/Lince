namespace Lince.Compiler.Sintaxis;

public enum TipoToken
{
    // Especiales
    FinDeArchivo,
    Invalido,

    // Identificadores y literales
    Identificador,
    LiteralEntero,
    LiteralDecimal,
    LiteralTexto,

    // Palabras clave
    PalabraFuncion,
    PalabraSea,
    PalabraVar,
    PalabraSi,
    PalabraSino,
    PalabraMientras,
    PalabraPara,
    PalabraEn,
    PalabraRegresar,
    PalabraRomper,
    PalabraContinuar,
    PalabraVerdadero,
    PalabraFalso,
    PalabraNulo,
    PalabraEntero,
    PalabraDecimal,
    PalabraTexto,
    PalabraLogico,
    PalabraNada,
    PalabraClase,
    PalabraEstructura,
    PalabraNuevo,
    PalabraEste,
    PalabraPrivado,
    PalabraPublico,
    PalabraLista,
    PalabraMapa,
    PalabraImportar,

    /// <summary>Reservada para fases siguientes; usarla hoy es un error de sintaxis.</summary>
    PalabraReservada,

    // Signos
    ParentesisAbre,
    ParentesisCierra,
    LlaveAbre,
    LlaveCierra,
    CorcheteAbre,
    CorcheteCierra,
    Coma,
    PuntoYComa,
    DosPuntos,
    Punto,
    PuntoPunto,
    Interrogacion,

    /// <summary><c>=&gt;</c>, la flecha de las lambdas.</summary>
    Flecha,

    // Operadores aritméticos
    Mas,
    Menos,
    Asterisco,
    Barra,
    Porcentaje,

    // Asignación
    Igual,
    MasIgual,
    MenosIgual,
    AsteriscoIgual,
    BarraIgual,
    PorcentajeIgual,

    // Comparación
    IgualIgual,
    Distinto,
    Menor,
    MenorIgual,
    Mayor,
    MayorIgual,

    // Lógicos
    YLogico,
    OLogico,
    Negacion,
}
