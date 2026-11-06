using System.Collections.Immutable;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Sintaxis;

/// <summary>
/// Analizador de descenso recursivo con recuperación de errores. Cuando falta un token lo
/// informa una sola vez por posición, lo inventa (<see cref="Token.EsFaltante"/>) y sigue;
/// cuando una sentencia queda mal, salta hasta el siguiente punto y coma o llave para
/// retomar en la siguiente. Así un archivo con cinco errores los muestra los cinco.
/// </summary>
public sealed class AnalizadorSintactico
{
    private readonly ArchivoFuente _archivo;
    private readonly ImmutableArray<Token> _tokens;
    private readonly BolsaDiagnosticos _diagnosticos;
    private int _posicion;
    private int _posicionDelUltimoError = -1;

    public AnalizadorSintactico(ArchivoFuente archivo, ImmutableArray<Token> tokens, BolsaDiagnosticos diagnosticos)
    {
        _archivo = archivo;
        _tokens = tokens;
        _diagnosticos = diagnosticos;
    }

    private Token Actual => Mirar(0);

    /// <summary>El último token consumido (o el primero del archivo si no se ha consumido ninguno).</summary>
    private Token Anterior => _tokens[Math.Max(0, _posicion - 1)];

    private Token Mirar(int desplazamiento)
    {
        var indice = _posicion + desplazamiento;
        return indice < _tokens.Length ? _tokens[indice] : _tokens[^1];
    }

    private Token Avanzar()
    {
        var token = Actual;
        if (_posicion < _tokens.Length - 1)
        {
            _posicion++;
        }

        return token;
    }

    /// <summary>
    /// Consume el token esperado o, si no está, informa y devuelve uno faltante de longitud
    /// cero en la posición actual, sin consumir nada: el token real queda para quien lo entienda.
    /// </summary>
    private Token Coincidir(TipoToken tipo)
    {
        if (Actual.Tipo == tipo)
        {
            return Avanzar();
        }

        InformarInesperado($"se esperaba {Palabras.Describir(tipo)}");
        return Faltante(tipo);
    }

    private Token Faltante(TipoToken tipo) =>
        new(tipo, Palabras.TextoFijo(tipo), null, new Ubicacion(_archivo, Actual.Ubicacion.Inicio, 0), EsFaltante: true);

    private void InformarInesperado(string queSeEsperaba)
    {
        // Un solo diagnóstico por posición: varios "se esperaba" sobre el mismo token confunden.
        if (_posicionDelUltimoError == _posicion)
        {
            return;
        }

        _posicionDelUltimoError = _posicion;

        if (Actual.Tipo == TipoToken.PalabraReservada)
        {
            _diagnosticos.Error(Codigos.PalabraReservada, Actual.Ubicacion,
                $"'{Actual.Texto}' es una palabra reservada para una versión futura del lenguaje y no puede usarse aquí");
            return;
        }

        var encontrado = Actual.Tipo switch
        {
            TipoToken.FinDeArchivo => "el fin del archivo",
            TipoToken.Invalido => $"'{Actual.Texto}'",
            _ => $"'{Actual.Texto}'",
        };
        _diagnosticos.Error(Codigos.TokenInesperado, Actual.Ubicacion, $"{queSeEsperaba} pero se encontró {encontrado}");
    }

    // ----- Programa y funciones -----

    public ProgramaSintaxis AnalizarPrograma()
    {
        var funciones = ImmutableArray.CreateBuilder<FuncionSintaxis>();
        while (Actual.Tipo != TipoToken.FinDeArchivo)
        {
            if (Actual.Tipo == TipoToken.PalabraFuncion)
            {
                funciones.Add(AnalizarFuncion());
                continue;
            }

            InformarDeclaracionEsperada();
            // Saltar hasta la siguiente función para no arrastrar el error por todo el archivo.
            while (Actual.Tipo is not (TipoToken.PalabraFuncion or TipoToken.FinDeArchivo))
            {
                Avanzar();
            }
        }

        return new ProgramaSintaxis(_archivo, funciones.ToImmutable(), Coincidir(TipoToken.FinDeArchivo));
    }

    private void InformarDeclaracionEsperada()
    {
        if (_posicionDelUltimoError == _posicion)
        {
            return;
        }

        _posicionDelUltimoError = _posicion;
        var pista = Actual.Tipo is TipoToken.PalabraSea or TipoToken.PalabraVar
            ? " (las variables solo pueden declararse dentro de una función)"
            : "";
        _diagnosticos.Error(Codigos.SeEsperabaDeclaracion, Actual.Ubicacion,
            $"se esperaba 'funcion' pero se encontró '{Actual.Texto}'{pista}");
    }

    private FuncionSintaxis AnalizarFuncion()
    {
        var palabra = Coincidir(TipoToken.PalabraFuncion);
        var nombre = Coincidir(TipoToken.Identificador);
        var abre = Coincidir(TipoToken.ParentesisAbre);
        var parametros = AnalizarParametros();
        var cierra = Coincidir(TipoToken.ParentesisCierra);

        TipoSintaxis? tipoRetorno = null;
        if (Actual.Tipo == TipoToken.DosPuntos)
        {
            Avanzar();
            tipoRetorno = AnalizarTipo();
        }

        var cuerpo = AnalizarBloque();
        return new FuncionSintaxis(palabra, nombre, abre, parametros, cierra, tipoRetorno, cuerpo);
    }

    private ImmutableArray<ParametroSintaxis> AnalizarParametros()
    {
        var parametros = ImmutableArray.CreateBuilder<ParametroSintaxis>();
        if (Actual.Tipo != TipoToken.Identificador)
        {
            // Sin parámetros, o algo que no es un parámetro: que el ')' esperado lo informe quien lo pide.
            return parametros.ToImmutable();
        }

        while (true)
        {
            var nombre = Coincidir(TipoToken.Identificador);
            var dosPuntos = Coincidir(TipoToken.DosPuntos);
            var tipo = AnalizarTipo();
            parametros.Add(new ParametroSintaxis(nombre, dosPuntos, tipo));

            if (Actual.Tipo == TipoToken.Coma)
            {
                Avanzar();
                continue;
            }

            if (Actual.Tipo is TipoToken.ParentesisCierra or TipoToken.LlaveAbre or TipoToken.FinDeArchivo)
            {
                break;
            }

            // Algo raro entre parámetros: informar y salir para que ')' se trate arriba.
            InformarInesperado("se esperaba ',' o ')'");
            break;
        }

        return parametros.ToImmutable();
    }

    private TipoSintaxis AnalizarTipo()
    {
        if (Palabras.EsTipo(Actual.Tipo))
        {
            return new TipoSintaxis(Avanzar());
        }

        if (_posicionDelUltimoError != _posicion)
        {
            _posicionDelUltimoError = _posicion;
            _diagnosticos.Error(Codigos.SeEsperabaTipo, Actual.Ubicacion,
                $"se esperaba un tipo (entero, decimal, texto, logico o nada) pero se encontró '{Actual.Texto}'");
        }

        var faltante = Faltante(TipoToken.PalabraNada);
        if (Actual.Tipo == TipoToken.Identificador)
        {
            // Escribió un nombre de tipo que no existe: se consume para que el resto de la
            // firma se lea bien y el error quede en uno solo.
            Avanzar();
        }

        return new TipoSintaxis(faltante);
    }

    // ----- Sentencias -----

    private BloqueSintaxis AnalizarBloque()
    {
        var abre = Coincidir(TipoToken.LlaveAbre);
        var sentencias = ImmutableArray.CreateBuilder<SentenciaSintaxis>();

        while (Actual.Tipo is not (TipoToken.LlaveCierra or TipoToken.FinDeArchivo))
        {
            var posicionInicial = _posicion;
            var erroresAntes = _diagnosticos.Cantidad;

            sentencias.Add(AnalizarSentencia());

            // Solo se resincroniza si la sentencia no terminó en un límite real (';' o '}'):
            // si lo hizo, lo que sigue es otra sentencia y se analiza normalmente.
            if (_diagnosticos.Cantidad > erroresAntes && Anterior.Tipo is not (TipoToken.PuntoYComa or TipoToken.LlaveCierra))
            {
                Sincronizar();
            }

            // Garantía de avance: si la sentencia no consumió nada, saltar el token culpable.
            if (_posicion == posicionInicial)
            {
                Avanzar();
            }
        }

        var cierra = Coincidir(TipoToken.LlaveCierra);
        return new BloqueSintaxis(abre, sentencias.ToImmutable(), cierra);
    }

    /// <summary>Tras un error, avanza hasta el fin de la sentencia o el inicio de la siguiente.</summary>
    private void Sincronizar()
    {
        while (Actual.Tipo != TipoToken.FinDeArchivo)
        {
            if (Actual.Tipo == TipoToken.PuntoYComa)
            {
                Avanzar();
                return;
            }

            if (Actual.Tipo is TipoToken.LlaveCierra or TipoToken.LlaveAbre
                or TipoToken.PalabraSea or TipoToken.PalabraVar or TipoToken.PalabraSi
                or TipoToken.PalabraMientras or TipoToken.PalabraPara or TipoToken.PalabraRegresar
                or TipoToken.PalabraRomper or TipoToken.PalabraContinuar or TipoToken.PalabraFuncion)
            {
                return;
            }

            Avanzar();
        }
    }

    private SentenciaSintaxis AnalizarSentencia()
    {
        switch (Actual.Tipo)
        {
            case TipoToken.LlaveAbre:
                return AnalizarBloque();
            case TipoToken.PalabraSea:
            case TipoToken.PalabraVar:
                return AnalizarDeclaracionVariable();
            case TipoToken.PalabraSi:
                return AnalizarSi();
            case TipoToken.PalabraMientras:
                return AnalizarMientras();
            case TipoToken.PalabraPara:
                return AnalizarPara();
            case TipoToken.PalabraRegresar:
                return AnalizarRegresar();
            case TipoToken.PalabraRomper:
                return new RomperSintaxis(Avanzar(), Coincidir(TipoToken.PuntoYComa));
            case TipoToken.PalabraContinuar:
                return new ContinuarSintaxis(Avanzar(), Coincidir(TipoToken.PuntoYComa));
            case TipoToken.Identificador when EsOperadorDeAsignacion(Mirar(1).Tipo):
                return AnalizarAsignacion();
            default:
                return AnalizarExpresionSentencia();
        }
    }

    private static bool EsOperadorDeAsignacion(TipoToken tipo) => tipo is TipoToken.Igual or TipoToken.MasIgual
        or TipoToken.MenosIgual or TipoToken.AsteriscoIgual or TipoToken.BarraIgual or TipoToken.PorcentajeIgual;

    private DeclaracionVariableSintaxis AnalizarDeclaracionVariable()
    {
        var palabra = Avanzar();
        var nombre = Coincidir(TipoToken.Identificador);

        TipoSintaxis? tipo = null;
        if (Actual.Tipo == TipoToken.DosPuntos)
        {
            Avanzar();
            tipo = AnalizarTipo();
        }

        var igual = Coincidir(TipoToken.Igual);
        var valor = AnalizarExpresion();
        var puntoYComa = Coincidir(TipoToken.PuntoYComa);
        return new DeclaracionVariableSintaxis(palabra, nombre, tipo, igual, valor, puntoYComa);
    }

    private AsignacionSintaxis AnalizarAsignacion()
    {
        var nombre = Avanzar();
        var operador = Avanzar();
        var valor = AnalizarExpresion();
        var puntoYComa = Coincidir(TipoToken.PuntoYComa);
        return new AsignacionSintaxis(nombre, operador, valor, puntoYComa);
    }

    private SiSintaxis AnalizarSi()
    {
        var palabraSi = Avanzar();
        var condicion = AnalizarExpresion();
        var entonces = AnalizarBloque();

        Token? palabraSino = null;
        SentenciaSintaxis? sino = null;
        if (Actual.Tipo == TipoToken.PalabraSino)
        {
            palabraSino = Avanzar();
            sino = Actual.Tipo == TipoToken.PalabraSi ? AnalizarSi() : AnalizarBloque();
        }

        return new SiSintaxis(palabraSi, condicion, entonces, palabraSino, sino);
    }

    private MientrasSintaxis AnalizarMientras()
    {
        var palabra = Avanzar();
        var condicion = AnalizarExpresion();
        var cuerpo = AnalizarBloque();
        return new MientrasSintaxis(palabra, condicion, cuerpo);
    }

    private ParaSintaxis AnalizarPara()
    {
        var palabraPara = Avanzar();
        var variable = Coincidir(TipoToken.Identificador);
        var palabraEn = Coincidir(TipoToken.PalabraEn);
        var desde = AnalizarExpresion();
        var puntoPunto = Coincidir(TipoToken.PuntoPunto);
        var hasta = AnalizarExpresion();
        var cuerpo = AnalizarBloque();
        return new ParaSintaxis(palabraPara, variable, palabraEn, desde, puntoPunto, hasta, cuerpo);
    }

    private RegresarSintaxis AnalizarRegresar()
    {
        var palabra = Avanzar();
        ExpresionSintaxis? valor = null;
        if (Actual.Tipo != TipoToken.PuntoYComa && Actual.Tipo != TipoToken.LlaveCierra && Actual.Tipo != TipoToken.FinDeArchivo)
        {
            valor = AnalizarExpresion();
        }

        var puntoYComa = Coincidir(TipoToken.PuntoYComa);
        return new RegresarSintaxis(palabra, valor, puntoYComa);
    }

    private ExpresionSentenciaSintaxis AnalizarExpresionSentencia()
    {
        var expresion = AnalizarExpresion();
        var puntoYComa = Coincidir(TipoToken.PuntoYComa);
        return new ExpresionSentenciaSintaxis(expresion, puntoYComa);
    }

    // ----- Expresiones (precedencia ascendente) -----

    public ExpresionSintaxis AnalizarExpresion() => AnalizarBinaria(0);

    /// <summary>Precedencia de un operador binario; 0 si el token no es uno.</summary>
    private static int Precedencia(TipoToken tipo) => tipo switch
    {
        TipoToken.OLogico => 1,
        TipoToken.YLogico => 2,
        TipoToken.IgualIgual or TipoToken.Distinto => 3,
        TipoToken.Menor or TipoToken.MenorIgual or TipoToken.Mayor or TipoToken.MayorIgual => 4,
        TipoToken.Mas or TipoToken.Menos => 5,
        TipoToken.Asterisco or TipoToken.Barra or TipoToken.Porcentaje => 6,
        _ => 0,
    };

    private const int PrecedenciaUnaria = 7;

    private ExpresionSintaxis AnalizarBinaria(int precedenciaMinima)
    {
        var izquierda = AnalizarUnaria();

        while (true)
        {
            var precedencia = Precedencia(Actual.Tipo);
            if (precedencia == 0 || precedencia <= precedenciaMinima)
            {
                return izquierda;
            }

            var operador = Avanzar();
            var derecha = AnalizarBinaria(precedencia);
            izquierda = new BinariaSintaxis(izquierda, operador, derecha);
        }
    }

    private ExpresionSintaxis AnalizarUnaria()
    {
        if (Actual.Tipo is TipoToken.Menos or TipoToken.Negacion)
        {
            var operador = Avanzar();
            var operando = AnalizarUnaria();
            return new UnariaSintaxis(operador, operando);
        }

        return AnalizarPostfija();
    }

    private ExpresionSintaxis AnalizarPostfija()
    {
        var expresion = AnalizarPrimaria();
        while (Actual.Tipo == TipoToken.ParentesisAbre)
        {
            var abre = Avanzar();
            var argumentos = AnalizarArgumentos();
            var cierra = Coincidir(TipoToken.ParentesisCierra);
            expresion = new LlamadaSintaxis(expresion, abre, argumentos, cierra);
        }

        return expresion;
    }

    private ImmutableArray<ExpresionSintaxis> AnalizarArgumentos()
    {
        var argumentos = ImmutableArray.CreateBuilder<ExpresionSintaxis>();
        if (Actual.Tipo is TipoToken.ParentesisCierra or TipoToken.FinDeArchivo)
        {
            return argumentos.ToImmutable();
        }

        while (true)
        {
            argumentos.Add(AnalizarExpresion());
            if (Actual.Tipo == TipoToken.Coma)
            {
                Avanzar();
                continue;
            }

            break;
        }

        return argumentos.ToImmutable();
    }

    private ExpresionSintaxis AnalizarPrimaria()
    {
        switch (Actual.Tipo)
        {
            case TipoToken.LiteralEntero:
            case TipoToken.LiteralDecimal:
            case TipoToken.LiteralTexto:
            case TipoToken.PalabraVerdadero:
            case TipoToken.PalabraFalso:
            case TipoToken.PalabraNulo:
                return new LiteralSintaxis(Avanzar());

            case TipoToken.Identificador:
                return new NombreSintaxis(Avanzar());

            case TipoToken.ParentesisAbre:
                {
                    var abre = Avanzar();
                    var expresion = AnalizarExpresion();
                    var cierra = Coincidir(TipoToken.ParentesisCierra);
                    return new ParentesisSintaxis(abre, expresion, cierra);
                }

            default:
                if (_posicionDelUltimoError != _posicion)
                {
                    _posicionDelUltimoError = _posicion;
                    if (Actual.Tipo == TipoToken.PalabraReservada)
                    {
                        _diagnosticos.Error(Codigos.PalabraReservada, Actual.Ubicacion,
                            $"'{Actual.Texto}' es una palabra reservada para una versión futura del lenguaje y no puede usarse aquí");
                    }
                    else
                    {
                        var encontrado = Actual.Tipo == TipoToken.FinDeArchivo ? "el fin del archivo" : $"'{Actual.Texto}'";
                        _diagnosticos.Error(Codigos.SeEsperabaExpresion, Actual.Ubicacion, $"se esperaba una expresión pero se encontró {encontrado}");
                    }
                }

                return new NombreSintaxis(Faltante(TipoToken.Identificador));
        }
    }
}
