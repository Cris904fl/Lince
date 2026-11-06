using System.Collections.Immutable;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Enlace;

/// <summary>
/// Resuelve nombres y comprueba tipos. Primero declara todas las funciones del archivo y
/// después enlaza cada cuerpo, así una función puede llamar a otra declarada más abajo.
/// Las expresiones con error reciben el tipo <see cref="TipoLince.Error"/> y no generan
/// más diagnósticos: un error, un mensaje.
/// </summary>
public sealed class Enlazador
{
    public const string NombreDePrincipal = "principal";
    public const string NombreDeImprimir = "imprimir";

    private readonly BolsaDiagnosticos _diagnosticos;
    private readonly Dictionary<string, SimboloFuncion> _funciones = new(StringComparer.Ordinal);
    private Ambito _ambito = new(null);
    private SimboloFuncion? _funcionActual;
    private int _profundidadDeCiclo;

    private Enlazador(BolsaDiagnosticos diagnosticos)
    {
        _diagnosticos = diagnosticos;
    }

    public static ProgramaEnlazado Enlazar(ArbolSintaxis arbol, BolsaDiagnosticos diagnosticos)
    {
        var enlazador = new Enlazador(diagnosticos);
        return enlazador.EnlazarPrograma(arbol.Raiz);
    }

    private ProgramaEnlazado EnlazarPrograma(ProgramaSintaxis programa)
    {
        foreach (var funcion in programa.Funciones)
        {
            DeclararFuncion(funcion);
        }

        var principal = ComprobarPrincipal(programa);

        var funciones = ImmutableArray.CreateBuilder<FuncionEnlazada>();
        foreach (var funcion in programa.Funciones)
        {
            if (!funcion.Nombre.EsFaltante && _funciones.TryGetValue(funcion.Nombre.Texto, out var simbolo) && simbolo.Declaracion == funcion)
            {
                funciones.Add(EnlazarFuncion(simbolo, funcion));
            }
            else
            {
                // Duplicada o sin nombre: se enlaza igual para informar sus errores, pero no se emite.
                EnlazarFuncion(CrearSimbolo(funcion), funcion);
            }
        }

        return new ProgramaEnlazado(funciones.ToImmutable(), principal);
    }

    // ----- Declaraciones -----

    private void DeclararFuncion(FuncionSintaxis funcion)
    {
        if (funcion.Nombre.EsFaltante)
        {
            return;
        }

        var nombre = funcion.Nombre.Texto;
        if (nombre == NombreDeImprimir)
        {
            _diagnosticos.Error(Codigos.NombreDeIntegrada, funcion.Nombre.Ubicacion,
                $"'{nombre}' es una función integrada del lenguaje y no se puede volver a declarar");
            return;
        }

        if (_funciones.ContainsKey(nombre))
        {
            _diagnosticos.Error(Codigos.FuncionDuplicada, funcion.Nombre.Ubicacion,
                $"ya existe una función llamada '{nombre}'; en esta versión no hay sobrecarga");
            return;
        }

        _funciones[nombre] = CrearSimbolo(funcion);
    }

    private SimboloFuncion CrearSimbolo(FuncionSintaxis funcion)
    {
        var parametros = ImmutableArray.CreateBuilder<SimboloVariable>();
        var nombresVistos = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < funcion.Parametros.Length; i++)
        {
            var parametro = funcion.Parametros[i];
            var tipo = ResolverTipo(parametro.Tipo);
            if (ReferenceEquals(tipo, TipoLince.Nada))
            {
                _diagnosticos.Error(Codigos.TipoIncompatible, parametro.Tipo.Ubicacion,
                    $"el parámetro '{parametro.Nombre.Texto}' no puede ser de tipo 'nada'");
                tipo = TipoLince.Error;
            }

            if (!parametro.Nombre.EsFaltante && !nombresVistos.Add(parametro.Nombre.Texto))
            {
                _diagnosticos.Error(Codigos.ParametroDuplicado, parametro.Nombre.Ubicacion,
                    $"la función '{funcion.Nombre.Texto}' ya tiene un parámetro llamado '{parametro.Nombre.Texto}'");
            }

            parametros.Add(new SimboloVariable(parametro.Nombre.Texto, tipo, esMutable: false, ClaseDeVariable.Parametro, i));
        }

        var tipoRetorno = funcion.TipoRetorno is null ? TipoLince.Nada : ResolverTipo(funcion.TipoRetorno);
        return new SimboloFuncion(funcion.Nombre.Texto, parametros.ToImmutable(), tipoRetorno, funcion);
    }

    private static TipoLince ResolverTipo(TipoSintaxis tipo) =>
        tipo.Palabra.EsFaltante ? TipoLince.Error : TipoLince.DesdePalabra(tipo.Palabra.Tipo) ?? TipoLince.Error;

    private SimboloFuncion? ComprobarPrincipal(ProgramaSintaxis programa)
    {
        if (!_funciones.TryGetValue(NombreDePrincipal, out var principal))
        {
            _diagnosticos.Error(Codigos.FaltaPrincipal, new Ubicacion(programa.Archivo, 0, 0),
                "el programa no tiene una función 'principal'; es el punto de entrada y debe existir");
            return null;
        }

        var declaracion = principal.Declaracion!;
        if (principal.Parametros.Length > 0)
        {
            _diagnosticos.Error(Codigos.PrincipalMalFormada, declaracion.Nombre.Ubicacion,
                "la función 'principal' no recibe parámetros en esta versión");
            return null;
        }

        if (!ReferenceEquals(principal.TipoRetorno, TipoLince.Nada) && !ReferenceEquals(principal.TipoRetorno, TipoLince.Entero)
            && !principal.TipoRetorno.EsError)
        {
            _diagnosticos.Error(Codigos.PrincipalMalFormada, declaracion.TipoRetorno!.Ubicacion,
                $"la función 'principal' debe regresar 'nada' o 'entero' (el código de salida), no '{principal.TipoRetorno}'");
            return null;
        }

        return principal;
    }

    private FuncionEnlazada EnlazarFuncion(SimboloFuncion simbolo, FuncionSintaxis declaracion)
    {
        _funcionActual = simbolo;
        _profundidadDeCiclo = 0;
        _ambito = new Ambito(null);
        foreach (var parametro in simbolo.Parametros)
        {
            // Si hay parámetros duplicados ya se informó; el primero gana.
            _ambito.Declarar(parametro);
        }

        var cuerpo = EnlazarBloque(declaracion.Cuerpo);

        if (!ReferenceEquals(simbolo.TipoRetorno, TipoLince.Nada) && !simbolo.TipoRetorno.EsError && !Termina(cuerpo))
        {
            _diagnosticos.Error(Codigos.NoTodosLosCaminosRegresan, declaracion.Nombre.Ubicacion,
                $"la función '{simbolo.Nombre}' regresa '{simbolo.TipoRetorno}' pero no todos los caminos llevan a un 'regresar'");
        }

        _funcionActual = null;
        return new FuncionEnlazada(simbolo, cuerpo);
    }

    // ----- Sentencias -----

    private BloqueEnlazado EnlazarBloque(BloqueSintaxis bloque)
    {
        _ambito = new Ambito(_ambito);
        var sentencias = ImmutableArray.CreateBuilder<SentenciaEnlazada>(bloque.Sentencias.Length);
        foreach (var sentencia in bloque.Sentencias)
        {
            sentencias.Add(EnlazarSentencia(sentencia));
        }

        _ambito = _ambito.Padre!;
        return new BloqueEnlazado(sentencias.MoveToImmutable());
    }

    private SentenciaEnlazada EnlazarSentencia(SentenciaSintaxis sentencia) => sentencia switch
    {
        BloqueSintaxis bloque => EnlazarBloque(bloque),
        DeclaracionVariableSintaxis declaracion => EnlazarDeclaracionVariable(declaracion),
        AsignacionSintaxis asignacion => EnlazarAsignacion(asignacion),
        SiSintaxis si => EnlazarSi(si),
        MientrasSintaxis mientras => EnlazarMientras(mientras),
        ParaSintaxis para => EnlazarPara(para),
        RegresarSintaxis regresar => EnlazarRegresar(regresar),
        RomperSintaxis romper => EnlazarSalidaDeCiclo(romper.PalabraRomper, new RomperEnlazado()),
        ContinuarSintaxis continuar => EnlazarSalidaDeCiclo(continuar.PalabraContinuar, new ContinuarEnlazado()),
        ExpresionSentenciaSintaxis expresion => EnlazarExpresionSentencia(expresion),
        _ => throw new InvalidOperationException($"Sentencia no contemplada: {sentencia.GetType().Name}"),
    };

    private SentenciaEnlazada EnlazarDeclaracionVariable(DeclaracionVariableSintaxis declaracion)
    {
        var valor = EnlazarExpresion(declaracion.Valor);
        TipoLince tipo;

        if (declaracion.Tipo is not null)
        {
            tipo = ResolverTipo(declaracion.Tipo);
            if (ReferenceEquals(tipo, TipoLince.Nada))
            {
                _diagnosticos.Error(Codigos.TipoIncompatible, declaracion.Tipo.Ubicacion, "una variable no puede ser de tipo 'nada'");
                tipo = TipoLince.Error;
            }

            valor = Convertir(valor, tipo, declaracion.Valor.Ubicacion,
                origen => $"no se puede guardar un valor de tipo '{origen}' en la variable '{declaracion.Nombre.Texto}' de tipo '{tipo}'");
        }
        else
        {
            tipo = InferirTipo(valor, declaracion.Valor.Ubicacion, declaracion.Nombre.Texto);
        }

        var variable = new SimboloVariable(declaracion.Nombre.Texto, tipo, declaracion.EsMutable, ClaseDeVariable.Local);
        DeclararVariable(variable, declaracion.Nombre);
        return new DeclaracionVariableEnlazada(variable, valor);
    }

    private TipoLince InferirTipo(ExpresionEnlazada valor, Ubicacion ubicacion, string nombre)
    {
        if (ReferenceEquals(valor.Tipo, TipoLince.Nulo))
        {
            _diagnosticos.Error(Codigos.NoSePuedeInferir, ubicacion,
                $"'nulo' no basta para saber el tipo de '{nombre}'; escríbalo: sea {nombre}: texto = nulo;");
            return TipoLince.Error;
        }

        if (ReferenceEquals(valor.Tipo, TipoLince.Nada))
        {
            _diagnosticos.Error(Codigos.ExpresionSinValor, ubicacion,
                $"la expresión no produce ningún valor que guardar en '{nombre}' (la función regresa 'nada')");
            return TipoLince.Error;
        }

        return valor.Tipo;
    }

    private void DeclararVariable(SimboloVariable variable, Token nombre)
    {
        if (nombre.EsFaltante)
        {
            return;
        }

        if (_funciones.ContainsKey(variable.Nombre) || variable.Nombre == NombreDeImprimir)
        {
            _diagnosticos.Error(Codigos.VariableDuplicada, nombre.Ubicacion,
                $"ya existe una función llamada '{variable.Nombre}'; elija otro nombre para la variable");
            return;
        }

        if (!_ambito.Declarar(variable))
        {
            _diagnosticos.Error(Codigos.VariableDuplicada, nombre.Ubicacion,
                $"ya existe una variable llamada '{variable.Nombre}' en este ámbito o en uno que lo contiene");
        }
    }

    private SentenciaEnlazada EnlazarAsignacion(AsignacionSintaxis asignacion)
    {
        var valor = EnlazarExpresion(asignacion.Valor);
        var variable = BuscarVariable(asignacion.Nombre);
        if (variable is null)
        {
            return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
        }

        if (!variable.EsMutable)
        {
            var motivo = variable.Clase switch
            {
                ClaseDeVariable.Parametro => "es un parámetro y los parámetros no cambian",
                ClaseDeVariable.Ciclo => "es la variable de un 'para' y la avanza el ciclo",
                _ => "se declaró con 'sea'; use 'var' si debe cambiar",
            };
            _diagnosticos.Error(Codigos.AsignacionAInmutable, asignacion.Nombre.Ubicacion,
                $"no se puede asignar a '{variable.Nombre}': {motivo}");
        }

        if (asignacion.Operador.Tipo != TipoToken.Igual)
        {
            var operador = asignacion.Operador.Tipo switch
            {
                TipoToken.MasIgual => TipoToken.Mas,
                TipoToken.MenosIgual => TipoToken.Menos,
                TipoToken.AsteriscoIgual => TipoToken.Asterisco,
                TipoToken.BarraIgual => TipoToken.Barra,
                _ => TipoToken.Porcentaje,
            };
            valor = EnlazarBinaria(new VariableEnlazada(variable), operador, valor, asignacion.Operador.Ubicacion);
        }

        valor = Convertir(valor, variable.Tipo, asignacion.Valor.Ubicacion,
            origen => $"no se puede asignar un valor de tipo '{origen}' a '{variable.Nombre}', que es de tipo '{variable.Tipo}'");
        return new AsignacionEnlazada(variable, valor);
    }

    private SiEnlazado EnlazarSi(SiSintaxis si)
    {
        var condicion = EnlazarCondicion(si.Condicion);
        var entonces = EnlazarBloque(si.Entonces);
        var sino = si.Sino is null ? null : EnlazarSentencia(si.Sino);
        return new SiEnlazado(condicion, entonces, sino);
    }

    private MientrasEnlazado EnlazarMientras(MientrasSintaxis mientras)
    {
        var condicion = EnlazarCondicion(mientras.Condicion);
        var cuerpo = EnlazarCuerpoDeCiclo(mientras.Cuerpo);
        return new MientrasEnlazado(condicion, cuerpo);
    }

    private ParaEnlazado EnlazarPara(ParaSintaxis para)
    {
        var desde = EnlazarLimiteDePara(para.Desde);
        var hasta = EnlazarLimiteDePara(para.Hasta);

        _ambito = new Ambito(_ambito);
        var variable = new SimboloVariable(para.Variable.Texto, TipoLince.Entero, esMutable: false, ClaseDeVariable.Ciclo);
        DeclararVariable(variable, para.Variable);
        var limite = new SimboloVariable("<limite>", TipoLince.Entero, esMutable: false, ClaseDeVariable.Oculta);
        var cuerpo = EnlazarCuerpoDeCiclo(para.Cuerpo);
        _ambito = _ambito.Padre!;

        return new ParaEnlazado(variable, limite, desde, hasta, cuerpo);
    }

    private ExpresionEnlazada EnlazarLimiteDePara(ExpresionSintaxis sintaxis)
    {
        var limite = EnlazarExpresion(sintaxis);
        if (!limite.Tipo.EsError && !ReferenceEquals(limite.Tipo, TipoLince.Entero))
        {
            _diagnosticos.Error(Codigos.LimiteDeParaNoEntero, sintaxis.Ubicacion,
                $"los límites de un 'para' deben ser 'entero' y este es '{limite.Tipo}'");
            return new ErrorEnlazado();
        }

        return limite;
    }

    private BloqueEnlazado EnlazarCuerpoDeCiclo(BloqueSintaxis cuerpo)
    {
        _profundidadDeCiclo++;
        var enlazado = EnlazarBloque(cuerpo);
        _profundidadDeCiclo--;
        return enlazado;
    }

    private ExpresionEnlazada EnlazarCondicion(ExpresionSintaxis sintaxis)
    {
        var condicion = EnlazarExpresion(sintaxis);
        if (!condicion.Tipo.EsError && !ReferenceEquals(condicion.Tipo, TipoLince.Logico))
        {
            _diagnosticos.Error(Codigos.CondicionNoLogica, sintaxis.Ubicacion,
                $"la condición debe ser 'logico' y esta expresión es '{condicion.Tipo}'");
            return new ErrorEnlazado();
        }

        return condicion;
    }

    private RegresarEnlazado EnlazarRegresar(RegresarSintaxis regresar)
    {
        var tipoRetorno = _funcionActual!.TipoRetorno;

        if (regresar.Valor is null)
        {
            if (!ReferenceEquals(tipoRetorno, TipoLince.Nada) && !tipoRetorno.EsError)
            {
                _diagnosticos.Error(Codigos.RegresarSinValor, regresar.PalabraRegresar.Ubicacion,
                    $"la función '{_funcionActual.Nombre}' regresa '{tipoRetorno}' y este 'regresar' no lleva valor");
            }

            return new RegresarEnlazado(null);
        }

        var valor = EnlazarExpresion(regresar.Valor);
        if (ReferenceEquals(tipoRetorno, TipoLince.Nada))
        {
            if (!valor.Tipo.EsError)
            {
                _diagnosticos.Error(Codigos.RegresarConValorEnNada, regresar.Valor.Ubicacion,
                    $"la función '{_funcionActual.Nombre}' regresa 'nada' y no puede devolver un valor");
            }

            return new RegresarEnlazado(null);
        }

        valor = Convertir(valor, tipoRetorno, regresar.Valor.Ubicacion,
            origen => $"la función '{_funcionActual.Nombre}' regresa '{tipoRetorno}' y este valor es '{origen}'",
            Codigos.TipoDeRetorno);
        return new RegresarEnlazado(valor);
    }

    private SentenciaEnlazada EnlazarSalidaDeCiclo(Token palabra, SentenciaEnlazada sentencia)
    {
        if (_profundidadDeCiclo == 0)
        {
            _diagnosticos.Error(Codigos.FueraDeCiclo, palabra.Ubicacion,
                $"'{palabra.Texto}' solo puede usarse dentro de un 'mientras' o un 'para'");
        }

        return sentencia;
    }

    private ExpresionSentenciaEnlazada EnlazarExpresionSentencia(ExpresionSentenciaSintaxis sentencia)
    {
        var expresion = EnlazarExpresion(sentencia.Expresion);
        if (expresion is not (LlamadaEnlazada or ImprimirEnlazado or ErrorEnlazado))
        {
            _diagnosticos.Error(Codigos.ExpresionSinEfecto, sentencia.Expresion.Ubicacion,
                "esta expresión no tiene efecto; solo una llamada a función puede ir sola como sentencia");
            return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
        }

        return new ExpresionSentenciaEnlazada(expresion);
    }

    // ----- Expresiones -----

    private ExpresionEnlazada EnlazarExpresion(ExpresionSintaxis expresion) => expresion switch
    {
        LiteralSintaxis literal => EnlazarLiteral(literal),
        NombreSintaxis nombre => EnlazarNombre(nombre),
        ParentesisSintaxis parentesis => EnlazarExpresion(parentesis.Expresion),
        UnariaSintaxis unaria => EnlazarUnaria(unaria),
        BinariaSintaxis binaria => EnlazarBinaria(
            EnlazarExpresion(binaria.Izquierda), binaria.Operador.Tipo, EnlazarExpresion(binaria.Derecha), binaria.Operador.Ubicacion),
        LlamadaSintaxis llamada => EnlazarLlamada(llamada),
        _ => throw new InvalidOperationException($"Expresión no contemplada: {expresion.GetType().Name}"),
    };

    private static ExpresionEnlazada EnlazarLiteral(LiteralSintaxis literal) => literal.Literal.Tipo switch
    {
        TipoToken.LiteralEntero => new LiteralEnlazado(literal.Literal.Valor, TipoLince.Entero),
        TipoToken.LiteralDecimal => new LiteralEnlazado(literal.Literal.Valor, TipoLince.Decimal),
        TipoToken.LiteralTexto => new LiteralEnlazado(literal.Literal.Valor, TipoLince.Texto),
        TipoToken.PalabraVerdadero => new LiteralEnlazado(true, TipoLince.Logico),
        TipoToken.PalabraFalso => new LiteralEnlazado(false, TipoLince.Logico),
        TipoToken.PalabraNulo => new LiteralEnlazado(null, TipoLince.Nulo),
        _ => new ErrorEnlazado(),
    };

    private ExpresionEnlazada EnlazarNombre(NombreSintaxis nombre)
    {
        var variable = BuscarVariable(nombre.Identificador);
        return variable is null ? new ErrorEnlazado() : new VariableEnlazada(variable);
    }

    /// <summary>Busca una variable visible; si no existe, informa y devuelve null.</summary>
    private SimboloVariable? BuscarVariable(Token identificador)
    {
        if (identificador.EsFaltante)
        {
            return null;
        }

        var variable = _ambito.Buscar(identificador.Texto);
        if (variable is not null)
        {
            return variable;
        }

        if (_funciones.ContainsKey(identificador.Texto) || identificador.Texto == NombreDeImprimir)
        {
            _diagnosticos.Error(Codigos.NombreNoEsVariable, identificador.Ubicacion,
                $"'{identificador.Texto}' es una función, no una variable; para llamarla escriba {identificador.Texto}(...)");
        }
        else
        {
            _diagnosticos.Error(Codigos.VariableNoExiste, identificador.Ubicacion,
                $"no existe una variable llamada '{identificador.Texto}'");
        }

        return null;
    }

    private ExpresionEnlazada EnlazarUnaria(UnariaSintaxis unaria)
    {
        var operando = EnlazarExpresion(unaria.Operando);
        if (operando.Tipo.EsError)
        {
            return new ErrorEnlazado();
        }

        if (unaria.Operador.Tipo == TipoToken.Menos && operando.Tipo.EsNumerico)
        {
            return new UnariaEnlazada(OperadorUnario.Negativo, operando, operando.Tipo);
        }

        if (unaria.Operador.Tipo == TipoToken.Negacion && ReferenceEquals(operando.Tipo, TipoLince.Logico))
        {
            return new UnariaEnlazada(OperadorUnario.NegacionLogica, operando, TipoLince.Logico);
        }

        _diagnosticos.Error(Codigos.OperadorUnarioInvalido, unaria.Operador.Ubicacion,
            $"el operador '{unaria.Operador.Texto}' no se puede aplicar a un valor de tipo '{operando.Tipo}'");
        return new ErrorEnlazado();
    }

    private ExpresionEnlazada EnlazarBinaria(ExpresionEnlazada izquierda, TipoToken operador, ExpresionEnlazada derecha, Ubicacion ubicacionOperador)
    {
        if (izquierda.Tipo.EsError || derecha.Tipo.EsError)
        {
            return new ErrorEnlazado();
        }

        var resultado = ResolverBinaria(izquierda, operador, derecha);
        if (resultado is not null)
        {
            return resultado;
        }

        _diagnosticos.Error(Codigos.OperadorBinarioInvalido, ubicacionOperador,
            $"el operador '{Palabras.TextoFijo(operador)}' no está definido entre '{izquierda.Tipo}' y '{derecha.Tipo}'");
        return new ErrorEnlazado();
    }

    private static ExpresionEnlazada? ResolverBinaria(ExpresionEnlazada izquierda, TipoToken operador, ExpresionEnlazada derecha)
    {
        var ti = izquierda.Tipo;
        var td = derecha.Tipo;
        var esTexto = ReferenceEquals(ti, TipoLince.Texto) || ReferenceEquals(td, TipoLince.Texto);

        switch (operador)
        {
            case TipoToken.Mas when esTexto && ti.SePuedeEscribir && td.SePuedeEscribir:
                return new BinariaEnlazada(ATexto(izquierda), OperadorBinario.Concatenacion, ATexto(derecha), TipoLince.Texto);

            case TipoToken.Mas or TipoToken.Menos or TipoToken.Asterisco or TipoToken.Barra or TipoToken.Porcentaje
                when ti.EsNumerico && td.EsNumerico:
                {
                    var tipo = TipoNumericoComun(ti, td);
                    var op = operador switch
                    {
                        TipoToken.Mas => OperadorBinario.Suma,
                        TipoToken.Menos => OperadorBinario.Resta,
                        TipoToken.Asterisco => OperadorBinario.Multiplicacion,
                        TipoToken.Barra => OperadorBinario.Division,
                        _ => OperadorBinario.Modulo,
                    };
                    return new BinariaEnlazada(ANumerico(izquierda, tipo), op, ANumerico(derecha, tipo), tipo);
                }

            case TipoToken.Menor or TipoToken.MenorIgual or TipoToken.Mayor or TipoToken.MayorIgual
                when ti.EsNumerico && td.EsNumerico:
                {
                    var tipo = TipoNumericoComun(ti, td);
                    var op = operador switch
                    {
                        TipoToken.Menor => OperadorBinario.Menor,
                        TipoToken.MenorIgual => OperadorBinario.MenorIgual,
                        TipoToken.Mayor => OperadorBinario.Mayor,
                        _ => OperadorBinario.MayorIgual,
                    };
                    return new BinariaEnlazada(ANumerico(izquierda, tipo), op, ANumerico(derecha, tipo), TipoLince.Logico);
                }

            case TipoToken.IgualIgual or TipoToken.Distinto:
                {
                    var op = operador == TipoToken.IgualIgual ? OperadorBinario.Igual : OperadorBinario.Distinto;
                    if (ti.EsNumerico && td.EsNumerico)
                    {
                        var tipo = TipoNumericoComun(ti, td);
                        return new BinariaEnlazada(ANumerico(izquierda, tipo), op, ANumerico(derecha, tipo), TipoLince.Logico);
                    }

                    if (ReferenceEquals(ti, TipoLince.Logico) && ReferenceEquals(td, TipoLince.Logico))
                    {
                        return new BinariaEnlazada(izquierda, op, derecha, TipoLince.Logico);
                    }

                    var esTextoONulo = (ReferenceEquals(ti, TipoLince.Texto) || ReferenceEquals(ti, TipoLince.Nulo))
                        && (ReferenceEquals(td, TipoLince.Texto) || ReferenceEquals(td, TipoLince.Nulo));
                    if (esTextoONulo)
                    {
                        return new BinariaEnlazada(ATexto(izquierda), op, ATexto(derecha), TipoLince.Logico);
                    }

                    return null;
                }

            case TipoToken.YLogico or TipoToken.OLogico
                when ReferenceEquals(ti, TipoLince.Logico) && ReferenceEquals(td, TipoLince.Logico):
                {
                    var op = operador == TipoToken.YLogico ? OperadorBinario.YLogico : OperadorBinario.OLogico;
                    return new BinariaEnlazada(izquierda, op, derecha, TipoLince.Logico);
                }

            default:
                return null;
        }
    }

    private static TipoLince TipoNumericoComun(TipoLince a, TipoLince b) =>
        ReferenceEquals(a, TipoLince.Decimal) || ReferenceEquals(b, TipoLince.Decimal) ? TipoLince.Decimal : TipoLince.Entero;

    private static ExpresionEnlazada ANumerico(ExpresionEnlazada expresion, TipoLince tipo) =>
        ReferenceEquals(expresion.Tipo, tipo) ? expresion : new ConversionEnlazada(expresion, tipo);

    /// <summary>Envuelve en una conversión a texto lo que no sea ya texto. Solo para tipos escribibles.</summary>
    private static ExpresionEnlazada ATexto(ExpresionEnlazada expresion) =>
        ReferenceEquals(expresion.Tipo, TipoLince.Texto) ? expresion : new ConversionEnlazada(expresion, TipoLince.Texto);

    private ExpresionEnlazada EnlazarLlamada(LlamadaSintaxis llamada)
    {
        if (llamada.Objetivo is not NombreSintaxis nombre)
        {
            _diagnosticos.Error(Codigos.FuncionNoExiste, llamada.Objetivo.Ubicacion,
                "solo se puede llamar a una función por su nombre");
            EnlazarArgumentos(llamada);
            return new ErrorEnlazado();
        }

        if (nombre.Identificador.EsFaltante)
        {
            EnlazarArgumentos(llamada);
            return new ErrorEnlazado();
        }

        var argumentos = EnlazarArgumentos(llamada);
        var texto = nombre.Identificador.Texto;

        if (texto == NombreDeImprimir)
        {
            return EnlazarImprimir(llamada, argumentos);
        }

        if (!_funciones.TryGetValue(texto, out var funcion))
        {
            if (_ambito.Buscar(texto) is not null)
            {
                _diagnosticos.Error(Codigos.NombreNoEsVariable, nombre.Ubicacion,
                    $"'{texto}' es una variable, no una función, y no se puede llamar");
            }
            else
            {
                _diagnosticos.Error(Codigos.FuncionNoExiste, nombre.Ubicacion, $"no existe una función llamada '{texto}'");
            }

            return new ErrorEnlazado();
        }

        if (argumentos.Length != funcion.Parametros.Length)
        {
            var esperados = funcion.Parametros.Length == 1 ? "1 argumento" : $"{funcion.Parametros.Length} argumentos";
            _diagnosticos.Error(Codigos.CantidadDeArgumentos, llamada.Ubicacion,
                $"la función '{texto}' espera {esperados} y recibió {argumentos.Length}");
            return new ErrorEnlazado();
        }

        var convertidos = ImmutableArray.CreateBuilder<ExpresionEnlazada>(argumentos.Length);
        var hayError = false;
        for (var i = 0; i < argumentos.Length; i++)
        {
            var parametro = funcion.Parametros[i];
            var indice = i + 1;
            var convertido = Convertir(argumentos[i], parametro.Tipo, llamada.Argumentos[i].Ubicacion,
                origen => $"el argumento {indice} de '{texto}' ('{parametro.Nombre}') debe ser '{parametro.Tipo}' y este es '{origen}'",
                Codigos.TipoDeArgumento);
            hayError |= convertido.Tipo.EsError;
            convertidos.Add(convertido);
        }

        return hayError ? new ErrorEnlazado() : new LlamadaEnlazada(funcion, convertidos.MoveToImmutable());
    }

    private ImmutableArray<ExpresionEnlazada> EnlazarArgumentos(LlamadaSintaxis llamada)
    {
        var argumentos = ImmutableArray.CreateBuilder<ExpresionEnlazada>(llamada.Argumentos.Length);
        foreach (var argumento in llamada.Argumentos)
        {
            argumentos.Add(EnlazarExpresion(argumento));
        }

        return argumentos.MoveToImmutable();
    }

    private ExpresionEnlazada EnlazarImprimir(LlamadaSintaxis llamada, ImmutableArray<ExpresionEnlazada> argumentos)
    {
        if (argumentos.Length == 0)
        {
            return new ImprimirEnlazado(null);
        }

        if (argumentos.Length > 1)
        {
            _diagnosticos.Error(Codigos.CantidadDeArgumentos, llamada.Ubicacion,
                $"'imprimir' recibe un solo valor y recibió {argumentos.Length}; concatene con '+' si quiere varios");
            return new ErrorEnlazado();
        }

        var valor = argumentos[0];
        if (valor.Tipo.EsError)
        {
            return new ErrorEnlazado();
        }

        if (!valor.Tipo.SePuedeEscribir)
        {
            _diagnosticos.Error(Codigos.ExpresionSinValor, llamada.Argumentos[0].Ubicacion,
                "la expresión no produce ningún valor que imprimir (la función regresa 'nada')");
            return new ErrorEnlazado();
        }

        return new ImprimirEnlazado(ATexto(valor));
    }

    // ----- Conversiones -----

    /// <summary>
    /// Aplica la conversión implícita al tipo destino o informa con el mensaje construido a
    /// partir del tipo de origen. Devuelve una expresión de error si no se puede.
    /// </summary>
    private ExpresionEnlazada Convertir(ExpresionEnlazada expresion, TipoLince destino, Ubicacion ubicacion,
        Func<TipoLince, string> mensaje, string codigo = Codigos.TipoIncompatible)
    {
        var origen = expresion.Tipo;
        if (origen.EsError || destino.EsError || ReferenceEquals(origen, destino))
        {
            return expresion;
        }

        if (ReferenceEquals(origen, TipoLince.Entero) && ReferenceEquals(destino, TipoLince.Decimal))
        {
            return new ConversionEnlazada(expresion, TipoLince.Decimal);
        }

        if (ReferenceEquals(origen, TipoLince.Nulo))
        {
            if (ReferenceEquals(destino, TipoLince.Texto))
            {
                return new ConversionEnlazada(expresion, TipoLince.Texto);
            }

            _diagnosticos.Error(Codigos.NuloEnTipoNoNulable, ubicacion, $"'nulo' no es un valor válido para el tipo '{destino}'; solo 'texto' admite nulo");
            return new ErrorEnlazado();
        }

        _diagnosticos.Error(codigo, ubicacion, mensaje(origen));
        return new ErrorEnlazado();
    }

    // ----- Análisis de caminos -----

    /// <summary>Verdadero si la sentencia nunca deja seguir a la siguiente: termina con regresar por todos los caminos.</summary>
    private static bool Termina(SentenciaEnlazada sentencia) => sentencia switch
    {
        RegresarEnlazado => true,
        BloqueEnlazado bloque => bloque.Sentencias.Any(Termina),
        SiEnlazado si => si.Sino is not null && Termina(si.Entonces) && Termina(si.Sino),
        MientrasEnlazado { Condicion: LiteralEnlazado { Valor: true } } mientras => !ContieneRomper(mientras.Cuerpo),
        _ => false,
    };

    /// <summary>Busca un romper que afecte a ESTE ciclo: no entra en ciclos anidados.</summary>
    private static bool ContieneRomper(SentenciaEnlazada sentencia) => sentencia switch
    {
        RomperEnlazado => true,
        BloqueEnlazado bloque => bloque.Sentencias.Any(ContieneRomper),
        SiEnlazado si => ContieneRomper(si.Entonces) || (si.Sino is not null && ContieneRomper(si.Sino)),
        _ => false,
    };

    // ----- Ámbitos -----

    private sealed class Ambito
    {
        private readonly Dictionary<string, SimboloVariable> _variables = new(StringComparer.Ordinal);

        public Ambito(Ambito? padre)
        {
            Padre = padre;
        }

        public Ambito? Padre { get; }

        public bool Declarar(SimboloVariable variable)
        {
            if (Buscar(variable.Nombre) is not null)
            {
                return false;
            }

            _variables[variable.Nombre] = variable;
            return true;
        }

        public SimboloVariable? Buscar(string nombre)
        {
            for (var ambito = this; ambito is not null; ambito = ambito.Padre)
            {
                if (ambito._variables.TryGetValue(nombre, out var variable))
                {
                    return variable;
                }
            }

            return null;
        }
    }
}
