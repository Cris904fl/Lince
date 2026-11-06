namespace Lince.Compiler.Diagnosticos;

/// <summary>
/// Códigos de diagnóstico. Rangos: LN1xxx léxico, LN2xxx sintaxis, LN3xxx tipos y enlace,
/// LN4xxx emisión y herramienta. Un código no se reutiliza para otro significado: si un
/// error desaparece, su número se retira.
/// </summary>
public static class Codigos
{
    // Léxico
    public const string TextoSinCerrar = "LN1001";
    public const string EscapeDesconocido = "LN1002";
    public const string NumeroFueraDeRango = "LN1003";
    public const string ComentarioSinCerrar = "LN1004";
    public const string CaracterInesperado = "LN1005";

    // Sintaxis
    public const string TokenInesperado = "LN2001";
    public const string SeEsperabaDeclaracion = "LN2002";
    public const string SeEsperabaExpresion = "LN2003";
    public const string PalabraReservada = "LN2004";
    public const string SeEsperabaTipo = "LN2005";
    public const string ObjetivoDeAsignacionInvalido = "LN2006";
    public const string SeEsperabaMiembro = "LN2007";

    // Enlace y tipos
    public const string FuncionDuplicada = "LN3001";
    public const string ParametroDuplicado = "LN3002";
    public const string FaltaPrincipal = "LN3003";
    public const string PrincipalMalFormada = "LN3004";
    public const string NombreDeIntegrada = "LN3005";
    public const string VariableDuplicada = "LN3010";
    public const string VariableNoExiste = "LN3011";
    public const string FuncionNoExiste = "LN3012";
    public const string CantidadDeArgumentos = "LN3013";
    public const string TipoDeArgumento = "LN3014";
    public const string AsignacionAInmutable = "LN3015";
    public const string TipoIncompatible = "LN3016";
    public const string CondicionNoLogica = "LN3017";
    public const string OperadorBinarioInvalido = "LN3018";
    public const string OperadorUnarioInvalido = "LN3019";
    public const string RegresarConValorEnNada = "LN3020";
    public const string RegresarSinValor = "LN3021";
    public const string TipoDeRetorno = "LN3022";
    public const string FueraDeCiclo = "LN3023";
    public const string ExpresionSinEfecto = "LN3024";
    public const string NoTodosLosCaminosRegresan = "LN3025";
    public const string NombreNoEsVariable = "LN3026";
    public const string NuloEnTipoNoNulable = "LN3027";
    public const string LimiteDeParaNoEntero = "LN3028";
    public const string NoSePuedeInferir = "LN3029";
    public const string ExpresionSinValor = "LN3030";
    public const string TipoNoExiste = "LN3031";
    public const string TipoDuplicado = "LN3032";
    public const string MiembroDuplicado = "LN3033";
    public const string MiembroNoExiste = "LN3034";
    public const string MiembroPrivado = "LN3035";
    public const string EsteFueraDeMetodo = "LN3036";
    public const string ModificadorFueraDeTipo = "LN3037";
    public const string ConstructorMalFormado = "LN3038";
    public const string CampoDeValorInmutable = "LN3039";
    public const string NuevoDeTipoBasico = "LN3040";
    public const string PuedeSerNulo = "LN3041";
    public const string MiembroDeNulable = "LN3042";
    public const string NuncaEsNulo = "LN3043";
    public const string NulableDeValor = "LN3044";
    public const string ArgumentosDeTipo = "LN3045";
    public const string ColeccionVaciaSinTipo = "LN3046";
    public const string NoSePuedeIndexar = "LN3047";
    public const string MiembroDeSoloLectura = "LN3048";
    public const string NoSePuedeRecorrer = "LN3049";
    public const string LambdaSinTipos = "LN3050";
    public const string LambdaSinRetorno = "LN3051";
    public const string NoEsFuncion = "LN3052";
    public const string ParametrosDeLambda = "LN3053";
    public const string ClaveNoComparable = "LN3054";
    public const string LambdaEnCampo = "LN3055";

    // Emisión y herramienta
    public const string ErrorDeEmision = "LN4001";
    public const string ArchivoNoEncontrado = "LN4002";
}
