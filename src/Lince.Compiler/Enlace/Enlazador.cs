using System.Collections.Immutable;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Sintaxis;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Enlace;

/// <summary>
/// Resuelve nombres y comprueba tipos en tres pasadas: declara los tipos, declara las
/// funciones y los miembros de cada tipo, y por último enlaza los cuerpos. Así cualquier
/// cuerpo puede usar cualquier tipo o función sin importar el orden del archivo.
/// Las expresiones con error reciben el tipo <see cref="TipoLince.Error"/> y no generan
/// más diagnósticos: un error, un mensaje.
/// </summary>
public sealed class Enlazador
{
    public const string NombreDePrincipal = "principal";
    public const string NombreDeImprimir = "imprimir";
    public const string NombreDeConstructor = "nuevo";

    private readonly BolsaDiagnosticos _diagnosticos;
    private readonly Dictionary<string, SimboloFuncion> _funciones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TipoUsuario> _tipos = new(StringComparer.Ordinal);
    private Ambito _ambito = new(null);
    private SimboloFuncion? _funcionActual;
    private TipoUsuario? _tipoActual;
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
        // Pasada 1: nombres de tipos, para que los campos y las firmas puedan referirse a ellos.
        foreach (var tipo in programa.Tipos)
        {
            DeclararTipo(tipo);
        }

        // Pasada 2: funciones libres y miembros de cada tipo.
        foreach (var funcion in programa.Funciones)
        {
            DeclararFuncion(funcion);
        }

        foreach (var tipo in programa.Tipos)
        {
            if (TipoDeclarado(tipo) is { } simbolo)
            {
                DeclararMiembros(simbolo);
            }
        }

        var principal = ComprobarPrincipal(programa);

        // Pasada 3: cuerpos.
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
                EnlazarFuncion(CrearSimboloDeFuncion(funcion, null), funcion);
            }
        }

        var tipos = ImmutableArray.CreateBuilder<TipoEnlazado>();
        foreach (var tipo in programa.Tipos)
        {
            if (TipoDeclarado(tipo) is { } simbolo)
            {
                tipos.Add(EnlazarTipo(simbolo));
            }
        }

        return new ProgramaEnlazado(funciones.ToImmutable(), tipos.ToImmutable(), principal);
    }

    /// <summary>El símbolo de una declaración de tipo, o null si esa declaración se descartó por duplicada o sin nombre.</summary>
    private TipoUsuario? TipoDeclarado(TipoDeclaradoSintaxis tipo) =>
        !tipo.Nombre.EsFaltante && _tipos.TryGetValue(tipo.Nombre.Texto, out var simbolo) && simbolo.Declaracion == tipo ? simbolo : null;

    // ----- Declaraciones -----

    private void DeclararTipo(TipoDeclaradoSintaxis tipo)
    {
        if (tipo.Nombre.EsFaltante)
        {
            return;
        }

        var nombre = tipo.Nombre.Texto;
        if (_tipos.ContainsKey(nombre))
        {
            _diagnosticos.Error(Codigos.TipoDuplicado, tipo.Nombre.Ubicacion, $"ya existe un tipo llamado '{nombre}'");
            return;
        }

        _tipos[nombre] = new TipoUsuario(nombre, tipo);
    }

    private void DeclararFuncion(FuncionSintaxis funcion)
    {
        if (funcion.Modificador is not null)
        {
            _diagnosticos.Error(Codigos.ModificadorFueraDeTipo, funcion.Modificador.Ubicacion,
                $"'{funcion.Modificador.Texto}' solo tiene sentido dentro de una clase o estructura");
        }

        if (funcion.Nombre.EsFaltante)
        {
            return;
        }

        var nombre = funcion.Nombre.Texto;
        if (funcion.EsConstructor)
        {
            _diagnosticos.Error(Codigos.ConstructorMalFormado, funcion.Nombre.Ubicacion,
                "'nuevo' solo puede declararse dentro de una clase o estructura, como su constructor");
            return;
        }

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

        _funciones[nombre] = CrearSimboloDeFuncion(funcion, null);
    }

    private void DeclararMiembros(TipoUsuario tipo)
    {
        foreach (var miembro in tipo.Declaracion.Miembros)
        {
            switch (miembro)
            {
                case CampoSintaxis campo:
                    DeclararCampo(tipo, campo);
                    break;
                case FuncionSintaxis metodo:
                    DeclararMetodo(tipo, metodo);
                    break;
            }
        }
    }

    private void DeclararCampo(TipoUsuario tipo, CampoSintaxis campo)
    {
        var tipoDelCampo = ResolverTipo(campo.Tipo);
        if (ReferenceEquals(tipoDelCampo, TipoLince.Nada))
        {
            _diagnosticos.Error(Codigos.TipoIncompatible, campo.Tipo.Ubicacion, $"el campo '{campo.Nombre.Texto}' no puede ser de tipo 'nada'");
            tipoDelCampo = TipoLince.Error;
        }

        if (campo.Nombre.EsFaltante)
        {
            return;
        }

        var simbolo = new SimboloCampo(campo.Nombre.Texto, tipoDelCampo, tipo, campo.EsPrivado, campo);
        if (!tipo.AgregarCampo(simbolo))
        {
            _diagnosticos.Error(Codigos.MiembroDuplicado, campo.Nombre.Ubicacion,
                $"la {tipo.Clase} '{tipo.Nombre}' ya tiene un miembro llamado '{campo.Nombre.Texto}'");
        }
    }

    private void DeclararMetodo(TipoUsuario tipo, FuncionSintaxis metodo)
    {
        if (metodo.Nombre.EsFaltante)
        {
            return;
        }

        var simbolo = CrearSimboloDeFuncion(metodo, tipo);

        if (metodo.EsConstructor)
        {
            if (metodo.TipoRetorno is not null)
            {
                _diagnosticos.Error(Codigos.ConstructorMalFormado, metodo.TipoRetorno.Ubicacion,
                    "el constructor 'nuevo' no lleva tipo de retorno");
            }

            if (tipo.ConstructorExplicito is not null)
            {
                _diagnosticos.Error(Codigos.MiembroDuplicado, metodo.Nombre.Ubicacion,
                    $"la {tipo.Clase} '{tipo.Nombre}' ya tiene un constructor; en esta versión no hay sobrecarga");
                return;
            }

            tipo.ConstructorExplicito = simbolo;
            return;
        }

        if (!tipo.AgregarMetodo(simbolo))
        {
            _diagnosticos.Error(Codigos.MiembroDuplicado, metodo.Nombre.Ubicacion,
                $"la {tipo.Clase} '{tipo.Nombre}' ya tiene un miembro llamado '{metodo.Nombre.Texto}'");
        }
    }

    private SimboloFuncion CrearSimboloDeFuncion(FuncionSintaxis funcion, TipoUsuario? contenedor)
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

        // Un constructor con tipo de retorno ya se informó (LN3038); se respeta lo escrito para no
        // añadir errores sobre sus 'regresar'.
        var tipoRetorno = funcion.TipoRetorno is null ? TipoLince.Nada : ResolverTipo(funcion.TipoRetorno);
        return new SimboloFuncion(funcion.Nombre.Texto, parametros.ToImmutable(), tipoRetorno, funcion, contenedor, funcion.EsPrivada);
    }

    private TipoLince ResolverTipo(TipoSintaxis tipo)
    {
        if (tipo.Palabra.EsFaltante)
        {
            return TipoLince.Error;
        }

        if (tipo.Palabra.Tipo == TipoToken.Identificador)
        {
            if (_tipos.TryGetValue(tipo.Palabra.Texto, out var usuario))
            {
                return usuario;
            }

            _diagnosticos.Error(Codigos.TipoNoExiste, tipo.Ubicacion, $"no existe un tipo llamado '{tipo.Palabra.Texto}'");
            return TipoLince.Error;
        }

        return TipoLince.DesdePalabra(tipo.Palabra.Tipo) ?? TipoLince.Error;
    }

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

    // ----- Cuerpos -----

    private FuncionEnlazada EnlazarFuncion(SimboloFuncion simbolo, FuncionSintaxis declaracion)
    {
        _funcionActual = simbolo;
        _tipoActual = simbolo.TipoContenedor;
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
        _tipoActual = null;
        return new FuncionEnlazada(simbolo, cuerpo);
    }

    private TipoEnlazado EnlazarTipo(TipoUsuario tipo)
    {
        // Los valores iniciales se enlazan fuera de todo método: no ven 'este' ni otros campos.
        var iniciales = ImmutableArray.CreateBuilder<CampoInicialEnlazado>();
        foreach (var campo in tipo.Campos)
        {
            if (campo.Declaracion.Inicial is null)
            {
                continue;
            }

            _ambito = new Ambito(null);
            var valor = EnlazarExpresion(campo.Declaracion.Inicial);
            valor = Convertir(valor, campo.Tipo, campo.Declaracion.Inicial.Ubicacion,
                origen => $"el valor inicial del campo '{campo.Nombre}' debe ser '{campo.Tipo}' y es '{origen}'");
            iniciales.Add(new CampoInicialEnlazado(campo, valor));
        }

        FuncionEnlazada? constructor = null;
        var metodos = ImmutableArray.CreateBuilder<FuncionEnlazada>();
        foreach (var metodo in tipo.Declaracion.Metodos)
        {
            if (metodo.Nombre.EsFaltante)
            {
                continue;
            }

            if (metodo.EsConstructor)
            {
                if (tipo.ConstructorExplicito?.Declaracion == metodo)
                {
                    constructor = EnlazarFuncion(tipo.ConstructorExplicito, metodo);
                }
                else
                {
                    EnlazarFuncion(CrearSimboloDeFuncion(metodo, tipo), metodo);
                }

                continue;
            }

            var simbolo = tipo.BuscarMetodo(metodo.Nombre.Texto);
            if (simbolo?.Declaracion == metodo)
            {
                metodos.Add(EnlazarFuncion(simbolo, metodo));
            }
            else
            {
                EnlazarFuncion(CrearSimboloDeFuncion(metodo, tipo), metodo);
            }
        }

        return new TipoEnlazado(tipo, iniciales.ToImmutable(), constructor, metodos.ToImmutable());
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

        // El objetivo: una variable, o un campo (a secas dentro de un método, o como objeto.campo).
        SimboloVariable? variable = null;
        ExpresionEnlazada? objeto = null;
        SimboloCampo? campo = null;

        switch (asignacion.Objetivo)
        {
            case NombreSintaxis nombre when !nombre.Identificador.EsFaltante:
                variable = _ambito.Buscar(nombre.Identificador.Texto);
                if (variable is null && _tipoActual?.BuscarCampo(nombre.Identificador.Texto) is { } campoPropio)
                {
                    objeto = new EsteEnlazado(_tipoActual);
                    campo = campoPropio;
                }
                else if (variable is null)
                {
                    BuscarVariable(nombre.Identificador); // informa el error adecuado
                    return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
                }

                break;

            case MiembroSintaxis miembro:
                objeto = EnlazarExpresion(miembro.Objeto);
                campo = BuscarCampoDe(objeto, miembro.Nombre);
                if (campo is null)
                {
                    return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
                }

                ComprobarObjetivoDeEstructura(objeto, miembro.Objeto.Ubicacion);
                break;

            default:
                // El analizador ya informó que el objetivo no es válido.
                return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
        }

        var tipoObjetivo = variable?.Tipo ?? campo!.Tipo;
        var nombreObjetivo = variable?.Nombre ?? campo!.Nombre;

        if (variable is not null && !variable.EsMutable)
        {
            var motivo = variable.Clase switch
            {
                ClaseDeVariable.Parametro => "es un parámetro y los parámetros no cambian",
                ClaseDeVariable.Ciclo => "es la variable de un 'para' y la avanza el ciclo",
                _ => "se declaró con 'sea'; use 'var' si debe cambiar",
            };
            _diagnosticos.Error(Codigos.AsignacionAInmutable, asignacion.Objetivo.Ubicacion,
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
            ExpresionEnlazada actual = variable is not null ? new VariableEnlazada(variable) : new CampoEnlazado(objeto!, campo!);
            valor = EnlazarBinaria(actual, operador, valor, asignacion.Operador.Ubicacion);
        }

        valor = Convertir(valor, tipoObjetivo, asignacion.Valor.Ubicacion,
            origen => $"no se puede asignar un valor de tipo '{origen}' a '{nombreObjetivo}', que es de tipo '{tipoObjetivo}'");

        return variable is not null
            ? new AsignacionEnlazada(variable, valor)
            : new AsignacionCampoEnlazada(objeto!, campo!, valor);
    }

    /// <summary>
    /// Cambiar un campo de una estructura solo tiene efecto si la estructura está en un sitio
    /// que se puede modificar: una variable 'var', 'este', o un campo de algo modificable. Si
    /// está en una variable 'sea', en un parámetro o es el resultado de una llamada, el cambio
    /// se haría sobre una copia y se perdería: se informa en vez de dejar un error silencioso.
    /// </summary>
    private void ComprobarObjetivoDeEstructura(ExpresionEnlazada objeto, Ubicacion ubicacion)
    {
        if (!objeto.Tipo.EsEstructura)
        {
            return;
        }

        switch (objeto)
        {
            case EsteEnlazado:
                return;
            case VariableEnlazada { Variable.EsMutable: true }:
                return;
            case VariableEnlazada { Variable: var variable }:
                var motivo = variable.Clase == ClaseDeVariable.Parametro ? "es un parámetro" : "se declaró con 'sea'";
                _diagnosticos.Error(Codigos.CampoDeValorInmutable, ubicacion,
                    $"no se puede cambiar un campo de '{variable.Nombre}': es una estructura y {motivo}; use 'var' si debe cambiar");
                return;
            case CampoEnlazado campo:
                ComprobarObjetivoDeEstructura(campo.Objeto, ubicacion);
                return;
            default:
                _diagnosticos.Error(Codigos.CampoDeValorInmutable, ubicacion,
                    "no se puede cambiar un campo de una estructura que no está guardada en una variable: el cambio se perdería");
                return;
        }
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
                var que = _funcionActual.EsConstructor ? "el constructor 'nuevo'" : $"la función '{_funcionActual.Nombre}'";
                _diagnosticos.Error(Codigos.RegresarConValorEnNada, regresar.Valor.Ubicacion,
                    $"{que} regresa 'nada' y no puede devolver un valor");
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
        if (expresion is not (LlamadaEnlazada or LlamadaMetodoEnlazada or ImprimirEnlazado or NuevoEnlazado or ErrorEnlazado))
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
        EsteSintaxis este => EnlazarEste(este),
        ParentesisSintaxis parentesis => EnlazarExpresion(parentesis.Expresion),
        UnariaSintaxis unaria => EnlazarUnaria(unaria),
        BinariaSintaxis binaria => EnlazarBinaria(
            EnlazarExpresion(binaria.Izquierda), binaria.Operador.Tipo, EnlazarExpresion(binaria.Derecha), binaria.Operador.Ubicacion),
        LlamadaSintaxis llamada => EnlazarLlamada(llamada),
        MiembroSintaxis miembro => EnlazarMiembro(miembro),
        NuevoSintaxis nuevo => EnlazarNuevo(nuevo),
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
        if (nombre.Identificador.EsFaltante)
        {
            return new ErrorEnlazado();
        }

        var variable = _ambito.Buscar(nombre.Identificador.Texto);
        if (variable is not null)
        {
            return new VariableEnlazada(variable);
        }

        // Dentro de un método, un campo del propio tipo se nombra a secas.
        if (_tipoActual?.BuscarCampo(nombre.Identificador.Texto) is { } campo)
        {
            return new CampoEnlazado(new EsteEnlazado(_tipoActual), campo);
        }

        BuscarVariable(nombre.Identificador);
        return new ErrorEnlazado();
    }

    private ExpresionEnlazada EnlazarEste(EsteSintaxis este)
    {
        if (_tipoActual is null)
        {
            _diagnosticos.Error(Codigos.EsteFueraDeMetodo, este.Ubicacion,
                "'este' solo puede usarse dentro de un método o constructor de una clase o estructura");
            return new ErrorEnlazado();
        }

        return new EsteEnlazado(_tipoActual);
    }

    /// <summary>Busca una variable visible; si no existe, informa el error que mejor describe lo que hay y devuelve null.</summary>
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

        var nombre = identificador.Texto;
        if (_funciones.ContainsKey(nombre) || nombre == NombreDeImprimir || _tipoActual?.BuscarMetodo(nombre) is not null)
        {
            _diagnosticos.Error(Codigos.NombreNoEsVariable, identificador.Ubicacion,
                $"'{nombre}' es una función, no una variable; para llamarla escriba {nombre}(...)");
        }
        else if (_tipos.ContainsKey(nombre))
        {
            _diagnosticos.Error(Codigos.NombreNoEsVariable, identificador.Ubicacion,
                $"'{nombre}' es un tipo, no una variable; para crear uno escriba nuevo {nombre}(...)");
        }
        else
        {
            _diagnosticos.Error(Codigos.VariableNoExiste, identificador.Ubicacion,
                $"no existe una variable llamada '{nombre}'");
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

                    // Dos referencias de la misma clase (o una y nulo) se comparan por identidad.
                    var clase = (ti as TipoUsuario ?? td as TipoUsuario);
                    if (clase is { EsEstructura: false }
                        && (ReferenceEquals(ti, clase) || ReferenceEquals(ti, TipoLince.Nulo))
                        && (ReferenceEquals(td, clase) || ReferenceEquals(td, TipoLince.Nulo)))
                    {
                        return new BinariaEnlazada(AReferencia(izquierda, clase), op, AReferencia(derecha, clase), TipoLince.Logico);
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

    private static ExpresionEnlazada AReferencia(ExpresionEnlazada expresion, TipoLince tipo) =>
        ReferenceEquals(expresion.Tipo, tipo) ? expresion : new ConversionEnlazada(expresion, tipo);

    private ExpresionEnlazada EnlazarLlamada(LlamadaSintaxis llamada)
    {
        switch (llamada.Objetivo)
        {
            case NombreSintaxis nombre:
                return EnlazarLlamadaPorNombre(llamada, nombre);

            case MiembroSintaxis miembro:
                return EnlazarLlamadaAMetodo(llamada, miembro);

            default:
                _diagnosticos.Error(Codigos.FuncionNoExiste, llamada.Objetivo.Ubicacion,
                    "solo se puede llamar a una función por su nombre o a un método como objeto.metodo(...)");
                EnlazarArgumentos(llamada);
                return new ErrorEnlazado();
        }
    }

    private ExpresionEnlazada EnlazarLlamadaPorNombre(LlamadaSintaxis llamada, NombreSintaxis nombre)
    {
        var argumentos = EnlazarArgumentos(llamada);
        if (nombre.Identificador.EsFaltante)
        {
            return new ErrorEnlazado();
        }

        var texto = nombre.Identificador.Texto;
        if (texto == NombreDeImprimir)
        {
            return EnlazarImprimir(llamada, argumentos);
        }

        // Dentro de un método, un método del propio tipo se llama a secas.
        if (_tipoActual?.BuscarMetodo(texto) is { } metodoPropio)
        {
            var convertidos = ConvertirArgumentos(metodoPropio, argumentos, llamada);
            return convertidos is null ? new ErrorEnlazado() : new LlamadaMetodoEnlazada(new EsteEnlazado(_tipoActual), metodoPropio, convertidos.Value);
        }

        if (!_funciones.TryGetValue(texto, out var funcion))
        {
            if (_ambito.Buscar(texto) is not null)
            {
                _diagnosticos.Error(Codigos.NombreNoEsVariable, nombre.Ubicacion,
                    $"'{texto}' es una variable, no una función, y no se puede llamar");
            }
            else if (_tipos.ContainsKey(texto))
            {
                _diagnosticos.Error(Codigos.NombreNoEsVariable, nombre.Ubicacion,
                    $"'{texto}' es un tipo; para crear uno escriba nuevo {texto}(...)");
            }
            else
            {
                _diagnosticos.Error(Codigos.FuncionNoExiste, nombre.Ubicacion, $"no existe una función llamada '{texto}'");
            }

            return new ErrorEnlazado();
        }

        var argumentosConvertidos = ConvertirArgumentos(funcion, argumentos, llamada);
        return argumentosConvertidos is null ? new ErrorEnlazado() : new LlamadaEnlazada(funcion, argumentosConvertidos.Value);
    }

    private ExpresionEnlazada EnlazarLlamadaAMetodo(LlamadaSintaxis llamada, MiembroSintaxis miembro)
    {
        var objeto = EnlazarExpresion(miembro.Objeto);
        var argumentos = EnlazarArgumentos(llamada);

        if (objeto.Tipo.EsError || miembro.Nombre.EsFaltante)
        {
            return new ErrorEnlazado();
        }

        if (objeto.Tipo is not TipoUsuario tipo)
        {
            _diagnosticos.Error(Codigos.MiembroNoExiste, miembro.Nombre.Ubicacion,
                $"el tipo '{objeto.Tipo}' no tiene métodos en esta versión del lenguaje");
            return new ErrorEnlazado();
        }

        var nombre = miembro.Nombre.Texto;
        var metodo = tipo.BuscarMetodo(nombre);
        if (metodo is null)
        {
            if (tipo.BuscarCampo(nombre) is not null)
            {
                _diagnosticos.Error(Codigos.NombreNoEsVariable, miembro.Nombre.Ubicacion,
                    $"'{nombre}' es un campo de '{tipo.Nombre}', no un método, y no se puede llamar");
            }
            else
            {
                _diagnosticos.Error(Codigos.MiembroNoExiste, miembro.Nombre.Ubicacion,
                    $"la {tipo.Clase} '{tipo.Nombre}' no tiene un método llamado '{nombre}'");
            }

            return new ErrorEnlazado();
        }

        if (metodo.EsPrivada && !ReferenceEquals(_tipoActual, tipo))
        {
            _diagnosticos.Error(Codigos.MiembroPrivado, miembro.Nombre.Ubicacion,
                $"el método '{nombre}' es privado de '{tipo.Nombre}' y solo se puede llamar desde dentro de la {tipo.Clase}");
            return new ErrorEnlazado();
        }

        var convertidos = ConvertirArgumentos(metodo, argumentos, llamada);
        return convertidos is null ? new ErrorEnlazado() : new LlamadaMetodoEnlazada(objeto, metodo, convertidos.Value);
    }

    private ImmutableArray<ExpresionEnlazada> EnlazarArgumentos(LlamadaSintaxis llamada) =>
        EnlazarArgumentos(llamada.Argumentos);

    private ImmutableArray<ExpresionEnlazada> EnlazarArgumentos(ImmutableArray<ExpresionSintaxis> sintaxis)
    {
        var argumentos = ImmutableArray.CreateBuilder<ExpresionEnlazada>(sintaxis.Length);
        foreach (var argumento in sintaxis)
        {
            argumentos.Add(EnlazarExpresion(argumento));
        }

        return argumentos.MoveToImmutable();
    }

    /// <summary>Comprueba cantidad y tipos de los argumentos contra los parámetros; null si algo falla (ya informado).</summary>
    private ImmutableArray<ExpresionEnlazada>? ConvertirArgumentos(SimboloFuncion funcion, ImmutableArray<ExpresionEnlazada> argumentos, LlamadaSintaxis llamada) =>
        ConvertirArgumentos(funcion.Nombre, funcion.Parametros, argumentos, llamada.Argumentos, llamada.Ubicacion);

    private ImmutableArray<ExpresionEnlazada>? ConvertirArgumentos(
        string nombre,
        ImmutableArray<SimboloVariable> parametros,
        ImmutableArray<ExpresionEnlazada> argumentos,
        ImmutableArray<ExpresionSintaxis> sintaxis,
        Ubicacion ubicacionLlamada)
    {
        if (argumentos.Length != parametros.Length)
        {
            var esperados = parametros.Length == 1 ? "1 argumento" : $"{parametros.Length} argumentos";
            _diagnosticos.Error(Codigos.CantidadDeArgumentos, ubicacionLlamada,
                $"'{nombre}' espera {esperados} y recibió {argumentos.Length}");
            return null;
        }

        var convertidos = ImmutableArray.CreateBuilder<ExpresionEnlazada>(argumentos.Length);
        var hayError = false;
        for (var i = 0; i < argumentos.Length; i++)
        {
            var parametro = parametros[i];
            var indice = i + 1;
            var convertido = Convertir(argumentos[i], parametro.Tipo, sintaxis[i].Ubicacion,
                origen => $"el argumento {indice} de '{nombre}' ('{parametro.Nombre}') debe ser '{parametro.Tipo}' y este es '{origen}'",
                Codigos.TipoDeArgumento);
            hayError |= convertido.Tipo.EsError;
            convertidos.Add(convertido);
        }

        return hayError ? null : convertidos.MoveToImmutable();
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

        if (ReferenceEquals(valor.Tipo, TipoLince.Nada))
        {
            _diagnosticos.Error(Codigos.ExpresionSinValor, llamada.Argumentos[0].Ubicacion,
                "la expresión no produce ningún valor que imprimir (la función regresa 'nada')");
            return new ErrorEnlazado();
        }

        if (!valor.Tipo.SePuedeEscribir)
        {
            _diagnosticos.Error(Codigos.TipoIncompatible, llamada.Argumentos[0].Ubicacion,
                $"no se puede imprimir un valor de tipo '{valor.Tipo}'; escriba un método que lo convierta a texto");
            return new ErrorEnlazado();
        }

        return new ImprimirEnlazado(ATexto(valor));
    }

    private ExpresionEnlazada EnlazarMiembro(MiembroSintaxis miembro)
    {
        var objeto = EnlazarExpresion(miembro.Objeto);
        var campo = BuscarCampoDe(objeto, miembro.Nombre);
        return campo is null ? new ErrorEnlazado() : new CampoEnlazado(objeto, campo);
    }

    /// <summary>El campo <c>nombre</c> del tipo del objeto, con sus comprobaciones; null (ya informado) si no procede.</summary>
    private SimboloCampo? BuscarCampoDe(ExpresionEnlazada objeto, Token nombre)
    {
        if (objeto.Tipo.EsError || nombre.EsFaltante)
        {
            return null;
        }

        if (objeto.Tipo is not TipoUsuario tipo)
        {
            _diagnosticos.Error(Codigos.MiembroNoExiste, nombre.Ubicacion,
                $"el tipo '{objeto.Tipo}' no tiene campos");
            return null;
        }

        var campo = tipo.BuscarCampo(nombre.Texto);
        if (campo is null)
        {
            if (tipo.BuscarMetodo(nombre.Texto) is not null)
            {
                _diagnosticos.Error(Codigos.NombreNoEsVariable, nombre.Ubicacion,
                    $"'{nombre.Texto}' es un método de '{tipo.Nombre}'; para llamarlo escriba .{nombre.Texto}(...)");
            }
            else
            {
                _diagnosticos.Error(Codigos.MiembroNoExiste, nombre.Ubicacion,
                    $"la {tipo.Clase} '{tipo.Nombre}' no tiene un campo llamado '{nombre.Texto}'");
            }

            return null;
        }

        if (campo.EsPrivado && !ReferenceEquals(_tipoActual, tipo))
        {
            _diagnosticos.Error(Codigos.MiembroPrivado, nombre.Ubicacion,
                $"el campo '{nombre.Texto}' es privado de '{tipo.Nombre}' y solo se puede usar desde dentro de la {tipo.Clase}");
            return null;
        }

        return campo;
    }

    private ExpresionEnlazada EnlazarNuevo(NuevoSintaxis nuevo)
    {
        var argumentos = EnlazarArgumentos(nuevo.Argumentos);
        if (nuevo.Tipo.EsFaltante)
        {
            return new ErrorEnlazado();
        }

        if (nuevo.Tipo.Tipo != TipoToken.Identificador)
        {
            _diagnosticos.Error(Codigos.NuevoDeTipoBasico, nuevo.Tipo.Ubicacion,
                $"'{nuevo.Tipo.Texto}' es un tipo básico y no se crea con 'nuevo'; escriba el valor directamente");
            return new ErrorEnlazado();
        }

        if (!_tipos.TryGetValue(nuevo.Tipo.Texto, out var tipo))
        {
            _diagnosticos.Error(Codigos.TipoNoExiste, nuevo.Tipo.Ubicacion, $"no existe un tipo llamado '{nuevo.Tipo.Texto}'");
            return new ErrorEnlazado();
        }

        var convertidos = ConvertirArgumentos($"nuevo {tipo.Nombre}", tipo.ParametrosDeNuevo, argumentos, nuevo.Argumentos, nuevo.Ubicacion);
        return convertidos is null ? new ErrorEnlazado() : new NuevoEnlazado(tipo, convertidos.Value);
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
            if (destino.EsReferencia)
            {
                return new ConversionEnlazada(expresion, destino);
            }

            _diagnosticos.Error(Codigos.NuloEnTipoNoNulable, ubicacion,
                $"'nulo' no es un valor válido para el tipo '{destino}'; solo 'texto' y las clases admiten nulo");
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
