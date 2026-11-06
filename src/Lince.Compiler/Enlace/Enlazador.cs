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

    /// <summary>
    /// Rutas (variable, o variable.campo.campo) que en el punto actual del cuerpo se sabe que
    /// no son nulas, por una comprobación anterior o una asignación de un valor no nulo. Es el
    /// estado del análisis de flujo de nulidad.
    /// </summary>
    private ImmutableHashSet<string> _noNulos = [];

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
        var baseTipo = ResolverTipoBase(tipo);
        if (!tipo.EsNulable || baseTipo.EsError)
        {
            return baseTipo;
        }

        if (!baseTipo.EsReferencia)
        {
            _diagnosticos.Error(Codigos.NulableDeValor, tipo.Ubicacion,
                $"'{baseTipo}?' no se admite en esta versión: solo 'texto' y las clases pueden ser nulables");
            return TipoLince.Error;
        }

        return TipoLince.Nulable(baseTipo);
    }

    private TipoLince ResolverTipoBase(TipoSintaxis tipo)
    {
        if (tipo.Palabra.EsFaltante)
        {
            return TipoLince.Error;
        }

        if (tipo.Palabra.Tipo is TipoToken.PalabraLista or TipoToken.PalabraMapa)
        {
            return ResolverColeccion(tipo);
        }

        if (tipo.Argumentos.Length > 0)
        {
            _diagnosticos.Error(Codigos.ArgumentosDeTipo, tipo.Ubicacion,
                $"'{tipo.Palabra.Texto}' no lleva argumentos de tipo; solo lista<T> y mapa<K, V> los llevan");
            return TipoLince.Error;
        }

        if (tipo.Palabra.Tipo == TipoToken.Identificador)
        {
            if (_tipos.TryGetValue(tipo.Palabra.Texto, out var usuario))
            {
                return usuario;
            }

            _diagnosticos.Error(Codigos.TipoNoExiste, tipo.Palabra.Ubicacion, $"no existe un tipo llamado '{tipo.Palabra.Texto}'");
            return TipoLince.Error;
        }

        return TipoLince.DesdePalabra(tipo.Palabra.Tipo) ?? TipoLince.Error;
    }

    private TipoLince ResolverColeccion(TipoSintaxis tipo)
    {
        var esLista = tipo.Palabra.Tipo == TipoToken.PalabraLista;
        var esperados = esLista ? 1 : 2;
        if (tipo.Argumentos.Length != esperados)
        {
            var forma = esLista ? "lista<T>, por ejemplo lista<entero>" : "mapa<K, V>, por ejemplo mapa<texto, entero>";
            _diagnosticos.Error(Codigos.ArgumentosDeTipo, tipo.Ubicacion,
                $"'{tipo.Palabra.Texto}' se escribe {forma}");
            return TipoLince.Error;
        }

        var argumentos = tipo.Argumentos.Select(ResolverTipo).ToImmutableArray();
        foreach (var (argumento, sintaxis) in argumentos.Zip(tipo.Argumentos))
        {
            if (ReferenceEquals(argumento, TipoLince.Nada))
            {
                _diagnosticos.Error(Codigos.TipoIncompatible, sintaxis.Ubicacion, "una colección no puede guardar valores de tipo 'nada'");
                return TipoLince.Error;
            }
        }

        if (argumentos.Any(a => a.EsError))
        {
            return TipoLince.Error;
        }

        return esLista ? TipoLince.Lista(argumentos[0]) : TipoLince.Mapa(argumentos[0], argumentos[1]);
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
        _noNulos = [];
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
            var valor = EnlazarExpresion(campo.Declaracion.Inicial, campo.Tipo);
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
        TipoLince tipo;
        ExpresionEnlazada valor;

        if (declaracion.Tipo is not null)
        {
            tipo = ResolverTipo(declaracion.Tipo);
            if (ReferenceEquals(tipo, TipoLince.Nada))
            {
                _diagnosticos.Error(Codigos.TipoIncompatible, declaracion.Tipo.Ubicacion, "una variable no puede ser de tipo 'nada'");
                tipo = TipoLince.Error;
            }

            // El tipo declarado guía a los literales de colección: sea l: lista<entero> = [];
            valor = EnlazarExpresion(declaracion.Valor, tipo);
            valor = Convertir(valor, tipo, declaracion.Valor.Ubicacion,
                origen => $"no se puede guardar un valor de tipo '{origen}' en la variable '{declaracion.Nombre.Texto}' de tipo '{tipo}'");
        }
        else
        {
            valor = EnlazarExpresion(declaracion.Valor);
            tipo = InferirTipo(valor, declaracion.Valor.Ubicacion, declaracion.Nombre.Texto);
        }

        var variable = new SimboloVariable(declaracion.Nombre.Texto, tipo, declaracion.EsMutable, ClaseDeVariable.Local);
        DeclararVariable(variable, declaracion.Nombre);
        AnotarAsignacion(RutaDeVariable(variable), tipo, valor);
        return new DeclaracionVariableEnlazada(variable, valor);
    }

    // ----- Análisis de flujo de nulidad -----

    /// <summary>
    /// Lo que una condición enseña sobre la nulidad: las rutas que no son nulas si la
    /// condición es verdadera y las que no lo son si es falsa.
    /// </summary>
    private readonly record struct Hechos(ImmutableHashSet<string> SiVerdadero, ImmutableHashSet<string> SiFalso)
    {
        public static readonly Hechos Nada = new([], []);
    }

    private static string RutaDeVariable(SimboloVariable variable) =>
        "v" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(variable);

    /// <summary>La ruta de una expresión enlazada que nombra un sitio (variable, este, o campo de un sitio); null si es un valor cualquiera.</summary>
    private static string? Ruta(ExpresionEnlazada expresion) => expresion switch
    {
        VariableEnlazada variable => RutaDeVariable(variable.Variable),
        EsteEnlazado => "este",
        CampoEnlazado campo => Ruta(campo.Objeto) is { } objeto ? objeto + "." + campo.Campo.Nombre : null,
        ConversionEnlazada conversion => Ruta(conversion.Expresion),
        _ => null,
    };

    /// <summary>La misma ruta, calculada desde la sintaxis con los nombres visibles en este punto.</summary>
    private string? RutaDeSintaxis(ExpresionSintaxis expresion) => expresion switch
    {
        ParentesisSintaxis parentesis => RutaDeSintaxis(parentesis.Expresion),
        EsteSintaxis => "este",
        NombreSintaxis nombre when _ambito.Buscar(nombre.Identificador.Texto) is { } variable => RutaDeVariable(variable),
        NombreSintaxis nombre when _tipoActual?.BuscarCampo(nombre.Identificador.Texto) is not null => "este." + nombre.Identificador.Texto,
        MiembroSintaxis miembro => RutaDeSintaxis(miembro.Objeto) is { } objeto ? objeto + "." + miembro.Nombre.Texto : null,
        _ => null,
    };

    /// <summary>Qué enseña una condición sobre la nulidad, leída de su sintaxis.</summary>
    private Hechos HechosDe(ExpresionSintaxis condicion)
    {
        switch (condicion)
        {
            case ParentesisSintaxis parentesis:
                return HechosDe(parentesis.Expresion);

            case UnariaSintaxis { Operador.Tipo: TipoToken.Negacion } negacion:
                var interior = HechosDe(negacion.Operando);
                return new Hechos(interior.SiFalso, interior.SiVerdadero);

            case BinariaSintaxis { Operador.Tipo: TipoToken.YLogico } y:
                {
                    var a = HechosDe(y.Izquierda);
                    var b = HechosDe(y.Derecha);
                    return new Hechos(a.SiVerdadero.Union(b.SiVerdadero), a.SiFalso.Intersect(b.SiFalso));
                }

            case BinariaSintaxis { Operador.Tipo: TipoToken.OLogico } o:
                {
                    var a = HechosDe(o.Izquierda);
                    var b = HechosDe(o.Derecha);
                    return new Hechos(a.SiVerdadero.Intersect(b.SiVerdadero), a.SiFalso.Union(b.SiFalso));
                }

            case BinariaSintaxis { Operador.Tipo: TipoToken.IgualIgual or TipoToken.Distinto } comparacion:
                {
                    var ruta = EsNulo(comparacion.Derecha) ? RutaDeSintaxis(comparacion.Izquierda)
                        : EsNulo(comparacion.Izquierda) ? RutaDeSintaxis(comparacion.Derecha)
                        : null;
                    if (ruta is null)
                    {
                        return Hechos.Nada;
                    }

                    ImmutableHashSet<string> conjunto = [ruta];
                    return comparacion.Operador.Tipo == TipoToken.Distinto ? new Hechos(conjunto, []) : new Hechos([], conjunto);
                }

            default:
                return Hechos.Nada;
        }
    }

    private static bool EsNulo(ExpresionSintaxis expresion) =>
        expresion is LiteralSintaxis { Literal.Tipo: TipoToken.PalabraNulo }
        || (expresion is ParentesisSintaxis parentesis && EsNulo(parentesis.Expresion));

    /// <summary>Si la expresión nombra una ruta nulable que se sabe no nula, la devuelve con su tipo sin '?'.</summary>
    private ExpresionEnlazada AplicarNoNulo(ExpresionEnlazada expresion)
    {
        if (expresion.Tipo is TipoNulable nulable && Ruta(expresion) is { } ruta && _noNulos.Contains(ruta))
        {
            return new ConversionEnlazada(expresion, nulable.Base);
        }

        return expresion;
    }

    /// <summary>
    /// Tras asignar a una ruta: lo que se sabía de ella y de sus campos deja de valer; si el
    /// valor asignado no puede ser nulo y el sitio sí, el sitio queda como no nulo.
    /// </summary>
    private void AnotarAsignacion(string? ruta, TipoLince tipoDelSitio, ExpresionEnlazada valor)
    {
        if (ruta is null)
        {
            return;
        }

        OlvidarRuta(ruta);

        // El valor llega envuelto en la conversión T → T?; lo que importa es el tipo de lo que hay dentro.
        var tipoDelValor = valor is ConversionEnlazada { Tipo.EsNulable: true } conversion ? conversion.Expresion.Tipo : valor.Tipo;
        if (tipoDelSitio.EsNulable && !tipoDelValor.EsNulable && !ReferenceEquals(tipoDelValor, TipoLince.Nulo) && !tipoDelValor.EsError)
        {
            _noNulos = _noNulos.Add(ruta);
        }
    }

    private void OlvidarRuta(string ruta) =>
        _noNulos = _noNulos.Except(_noNulos.Where(r => r == ruta || r.StartsWith(ruta + ".", StringComparison.Ordinal)));

    /// <summary>
    /// Antes de un ciclo: todo lo que el cuerpo asigna deja de saberse, porque en la segunda
    /// vuelta la ruta puede traer lo que el cuerpo le puso al final de la primera.
    /// </summary>
    private void OlvidarLoQueAsigna(BloqueSintaxis cuerpo)
    {
        foreach (var raiz in RaicesAsignadas(cuerpo))
        {
            OlvidarRuta(raiz);
        }
    }

    private IEnumerable<string> RaicesAsignadas(SentenciaSintaxis sentencia)
    {
        switch (sentencia)
        {
            case BloqueSintaxis bloque:
                return bloque.Sentencias.SelectMany(RaicesAsignadas);
            case SiSintaxis si:
                return RaicesAsignadas(si.Entonces).Concat(si.Sino is null ? [] : RaicesAsignadas(si.Sino));
            case MientrasSintaxis mientras:
                return RaicesAsignadas(mientras.Cuerpo);
            case ParaSintaxis para:
                return RaicesAsignadas(para.Cuerpo);
            case AsignacionSintaxis asignacion:
                var objetivo = asignacion.Objetivo;
                while (objetivo is MiembroSintaxis or IndiceSintaxis)
                {
                    objetivo = objetivo is MiembroSintaxis miembro ? miembro.Objeto : ((IndiceSintaxis)objetivo).Objeto;
                }

                return RutaDeSintaxis(objetivo) is { } raiz ? [raiz] : [];
            default:
                return [];
        }
    }

    private TipoLince InferirTipo(ExpresionEnlazada valor, Ubicacion ubicacion, string nombre)
    {
        if (ReferenceEquals(valor.Tipo, TipoLince.Nulo))
        {
            _diagnosticos.Error(Codigos.NoSePuedeInferir, ubicacion,
                $"'nulo' no basta para saber el tipo de '{nombre}'; escríbalo: sea {nombre}: texto? = nulo;");
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
        // El objetivo: una variable, un campo (a secas dentro de un método, o como objeto.campo)
        // o un elemento de colección (coleccion[indice]).
        SimboloVariable? variable = null;
        ExpresionEnlazada? objeto = null;
        SimboloCampo? campo = null;
        IndiceEnlazado? elemento = null;

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
                    EnlazarExpresion(asignacion.Valor);
                    return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
                }

                break;

            case MiembroSintaxis miembro:
                objeto = EnlazarExpresion(miembro.Objeto);
                if (objeto.Tipo is not TipoUsuario && !objeto.Tipo.EsError && !objeto.Tipo.EsNulable
                    && Integrados.Buscar(objeto.Tipo, miembro.Nombre.Texto) is { EsPropiedad: true })
                {
                    _diagnosticos.Error(Codigos.MiembroDeSoloLectura, miembro.Nombre.Ubicacion,
                        $"'{miembro.Nombre.Texto}' de '{objeto.Tipo}' es de solo lectura");
                    EnlazarExpresion(asignacion.Valor);
                    return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
                }

                campo = BuscarCampoDe(objeto, miembro.Nombre, miembro.Objeto);
                if (campo is null)
                {
                    EnlazarExpresion(asignacion.Valor);
                    return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
                }

                ComprobarObjetivoDeEstructura(objeto, miembro.Objeto.Ubicacion);
                break;

            case IndiceSintaxis indice:
                if (EnlazarIndice(indice) is not IndiceEnlazado indiceEnlazado)
                {
                    EnlazarExpresion(asignacion.Valor);
                    return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
                }

                elemento = indiceEnlazado;
                break;

            default:
                // El analizador ya informó que el objetivo no es válido.
                EnlazarExpresion(asignacion.Valor);
                return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
        }

        var tipoObjetivo = variable?.Tipo ?? campo?.Tipo ?? elemento!.Tipo;
        var nombreObjetivo = variable?.Nombre ?? campo?.Nombre ?? "el elemento";
        var valor = EnlazarExpresion(asignacion.Valor, asignacion.Operador.Tipo == TipoToken.Igual ? tipoObjetivo : null);

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
            ExpresionEnlazada actual = variable is not null ? new VariableEnlazada(variable)
                : campo is not null ? new CampoEnlazado(objeto!, campo)
                : elemento!;
            valor = EnlazarBinaria(actual, operador, valor, asignacion.Operador.Ubicacion);
        }

        valor = Convertir(valor, tipoObjetivo, asignacion.Valor.Ubicacion,
            origen => $"no se puede asignar un valor de tipo '{origen}' a '{nombreObjetivo}', que es de tipo '{tipoObjetivo}'");

        if (variable is not null)
        {
            AnotarAsignacion(RutaDeVariable(variable), tipoObjetivo, valor);
            return new AsignacionEnlazada(variable, valor);
        }

        if (elemento is not null)
        {
            return new AsignacionIndiceEnlazada(elemento.Objeto, elemento.Indice, valor);
        }

        AnotarAsignacion(Ruta(objeto!) is { } rutaObjeto ? rutaObjeto + "." + campo!.Nombre : null, tipoObjetivo, valor);
        return new AsignacionCampoEnlazada(objeto!, campo!, valor);
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
        var hechos = HechosDe(si.Condicion);
        var antes = _noNulos;

        _noNulos = antes.Union(hechos.SiVerdadero);
        var entonces = EnlazarBloque(si.Entonces);
        var despuesDeEntonces = _noNulos;

        _noNulos = antes.Union(hechos.SiFalso);
        var sino = si.Sino is null ? null : EnlazarSentencia(si.Sino);
        var despuesDeSino = _noNulos;

        // Lo que sigue al 'si' solo hereda lo de las ramas por las que se puede llegar: si una
        // rama termina (regresa), el resto del cuerpo viene de la otra.
        var terminaEntonces = Termina(entonces);
        var terminaSino = sino is not null && Termina(sino);
        _noNulos = (terminaEntonces, terminaSino) switch
        {
            (true, true) => antes,
            (true, false) => despuesDeSino,
            (false, true) => despuesDeEntonces,
            _ => despuesDeEntonces.Intersect(despuesDeSino),
        };

        return new SiEnlazado(condicion, entonces, sino);
    }

    private MientrasEnlazado EnlazarMientras(MientrasSintaxis mientras)
    {
        OlvidarLoQueAsigna(mientras.Cuerpo);
        var antes = _noNulos;
        var condicion = EnlazarCondicion(mientras.Condicion);
        var hechos = HechosDe(mientras.Condicion);

        _noNulos = antes.Union(hechos.SiVerdadero);
        var cuerpo = EnlazarCuerpoDeCiclo(mientras.Cuerpo);

        // Al salir, la condición fue falsa (salvo que se saliera con 'romper').
        _noNulos = ContieneRomper(cuerpo) ? antes : antes.Union(hechos.SiFalso);
        return new MientrasEnlazado(condicion, cuerpo);
    }

    private SentenciaEnlazada EnlazarPara(ParaSintaxis para)
    {
        if (!para.EsRango)
        {
            return EnlazarParaEnColeccion(para);
        }

        var desde = EnlazarLimiteDePara(para.Desde);
        var hasta = EnlazarLimiteDePara(para.Hasta!);
        OlvidarLoQueAsigna(para.Cuerpo);
        var antes = _noNulos;

        _ambito = new Ambito(_ambito);
        var variable = new SimboloVariable(para.Variable.Texto, TipoLince.Entero, esMutable: false, ClaseDeVariable.Ciclo);
        DeclararVariable(variable, para.Variable);
        var limite = new SimboloVariable("<limite>", TipoLince.Entero, esMutable: false, ClaseDeVariable.Oculta);
        var cuerpo = EnlazarCuerpoDeCiclo(para.Cuerpo);
        _ambito = _ambito.Padre!;
        _noNulos = antes;

        return new ParaEnlazado(variable, limite, desde, hasta, cuerpo);
    }

    /// <summary><c>para x en lista</c> recorre los elementos; <c>para k en mapa</c>, las claves (por su lista).</summary>
    private SentenciaEnlazada EnlazarParaEnColeccion(ParaSintaxis para)
    {
        var coleccion = EnlazarExpresion(para.Desde);
        TipoLince tipoElemento;

        if (coleccion.Tipo.EsError)
        {
            tipoElemento = TipoLince.Error;
        }
        else if (coleccion.Tipo.EsNulable)
        {
            InformarMiembroDeNulable(coleccion, para.Desde);
            tipoElemento = TipoLince.Error;
        }
        else if (coleccion.Tipo is TipoGenerico { EsLista: true } lista)
        {
            tipoElemento = lista.Elemento;
        }
        else if (coleccion.Tipo is TipoGenerico { EsMapa: true } mapa)
        {
            var claves = Integrados.Buscar(mapa, "claves")!;
            coleccion = new MiembroIntegradoEnlazado(coleccion, claves, []);
            tipoElemento = mapa.Argumentos[0];
        }
        else
        {
            _diagnosticos.Error(Codigos.NoSePuedeRecorrer, para.Desde.Ubicacion,
                $"'para ... en' recorre una lista, las claves de un mapa o un rango a..b, no un valor de tipo '{coleccion.Tipo}'");
            tipoElemento = TipoLince.Error;
        }

        OlvidarLoQueAsigna(para.Cuerpo);
        var antes = _noNulos;

        _ambito = new Ambito(_ambito);
        var variable = new SimboloVariable(para.Variable.Texto, tipoElemento, esMutable: false, ClaseDeVariable.Ciclo);
        DeclararVariable(variable, para.Variable);
        var copia = new SimboloVariable("<coleccion>", coleccion.Tipo, esMutable: false, ClaseDeVariable.Oculta);
        var indice = new SimboloVariable("<indice>", TipoLince.Entero, esMutable: false, ClaseDeVariable.Oculta);
        var cuerpo = EnlazarCuerpoDeCiclo(para.Cuerpo);
        _ambito = _ambito.Padre!;
        _noNulos = antes;

        return new ParaEnColeccionEnlazado(variable, copia, indice, coleccion, cuerpo);
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

        var valor = EnlazarExpresion(regresar.Valor, tipoRetorno);
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
        if (expresion is not (LlamadaEnlazada or LlamadaMetodoEnlazada or ImprimirEnlazado or NuevoEnlazado or ErrorEnlazado
            or MiembroIntegradoEnlazado { Miembro.EsPropiedad: false }))
        {
            _diagnosticos.Error(Codigos.ExpresionSinEfecto, sentencia.Expresion.Ubicacion,
                "esta expresión no tiene efecto; solo una llamada a función puede ir sola como sentencia");
            return new ExpresionSentenciaEnlazada(new ErrorEnlazado());
        }

        return new ExpresionSentenciaEnlazada(expresion);
    }

    // ----- Expresiones -----

    /// <param name="esperado">
    /// El tipo que el contexto espera (el declarado de una variable, el del parámetro, el de
    /// retorno). Solo lo usan los literales de colección, para saber de qué tipo es <c>[]</c>.
    /// </param>
    private ExpresionEnlazada EnlazarExpresion(ExpresionSintaxis expresion, TipoLince? esperado) => expresion switch
    {
        ListaLiteralSintaxis lista => EnlazarListaLiteral(lista, esperado),
        MapaLiteralSintaxis mapa => EnlazarMapaLiteral(mapa, esperado),
        ParentesisSintaxis parentesis => EnlazarExpresion(parentesis.Expresion, esperado),
        _ => EnlazarExpresion(expresion),
    };

    private ExpresionEnlazada EnlazarExpresion(ExpresionSintaxis expresion) => expresion switch
    {
        ListaLiteralSintaxis lista => EnlazarListaLiteral(lista, null),
        MapaLiteralSintaxis mapa => EnlazarMapaLiteral(mapa, null),
        IndiceSintaxis indice => EnlazarIndice(indice),
        LiteralSintaxis literal => EnlazarLiteral(literal),
        NombreSintaxis nombre => EnlazarNombre(nombre),
        EsteSintaxis este => EnlazarEste(este),
        ParentesisSintaxis parentesis => EnlazarExpresion(parentesis.Expresion),
        UnariaSintaxis unaria => EnlazarUnaria(unaria),
        BinariaSintaxis binaria => EnlazarBinariaSintaxis(binaria),
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
            return AplicarNoNulo(new VariableEnlazada(variable));
        }

        // Dentro de un método, un campo del propio tipo se nombra a secas.
        if (_tipoActual?.BuscarCampo(nombre.Identificador.Texto) is { } campo)
        {
            return AplicarNoNulo(new CampoEnlazado(new EsteEnlazado(_tipoActual), campo));
        }

        BuscarVariable(nombre.Identificador);
        return new ErrorEnlazado();
    }

    /// <summary>
    /// Un operador binario. En <c>a &amp;&amp; b</c>, b se enlaza sabiendo que a fue verdadera;
    /// en <c>a || b</c>, sabiendo que fue falsa: así <c>x != nulo &amp;&amp; x.campo > 0</c> compila.
    /// </summary>
    private ExpresionEnlazada EnlazarBinariaSintaxis(BinariaSintaxis binaria)
    {
        var izquierda = EnlazarExpresion(binaria.Izquierda);

        var antes = _noNulos;
        if (binaria.Operador.Tipo == TipoToken.YLogico)
        {
            _noNulos = antes.Union(HechosDe(binaria.Izquierda).SiVerdadero);
        }
        else if (binaria.Operador.Tipo == TipoToken.OLogico)
        {
            _noNulos = antes.Union(HechosDe(binaria.Izquierda).SiFalso);
        }

        var derecha = EnlazarExpresion(binaria.Derecha);
        _noNulos = antes;

        return EnlazarBinaria(izquierda, binaria.Operador.Tipo, derecha, binaria.Operador.Ubicacion);
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

        if (operador is TipoToken.IgualIgual or TipoToken.Distinto)
        {
            // Comparar con nulo algo que nunca es nulo es un despiste: o sobra la comparación o falta el '?'.
            // Cuenta el tipo declarado: una ruta que el flujo ya sabe no nula sigue pudiendo compararse.
            var (conNulo, otro) = ReferenceEquals(derecha.Tipo, TipoLince.Nulo) ? (derecha, izquierda) : (izquierda, derecha);
            var tipoDeclarado = otro is ConversionEnlazada { Expresion.Tipo.EsNulable: true } convertido ? convertido.Expresion.Tipo : otro.Tipo;
            if (ReferenceEquals(conNulo.Tipo, TipoLince.Nulo) && !tipoDeclarado.EsNulable && !ReferenceEquals(otro.Tipo, TipoLince.Nulo))
            {
                var pista = otro.Tipo.EsReferencia ? $"; si puede serlo, declárelo como '{otro.Tipo}?'" : "";
                _diagnosticos.Error(Codigos.NuncaEsNulo, ubicacionOperador,
                    $"esta expresión es de tipo '{otro.Tipo}' y nunca es nulo{pista}");
                return new ErrorEnlazado();
            }
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
        var esTexto = ti.EsTextual || td.EsTextual;

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

                    // Textos (con o sin '?') entre sí o con nulo: por contenido.
                    var esTextoONulo = (ti.EsTextual || ReferenceEquals(ti, TipoLince.Nulo))
                        && (td.EsTextual || ReferenceEquals(td, TipoLince.Nulo));
                    if (esTextoONulo)
                    {
                        return new BinariaEnlazada(ATexto(izquierda), op, ATexto(derecha), TipoLince.Logico);
                    }

                    // Dos referencias del mismo tipo (clase o colección, con o sin '?', o una y nulo): por identidad.
                    var referencia = EsReferenciaComparable(ti.SinNulo) ? ti.SinNulo : EsReferenciaComparable(td.SinNulo) ? td.SinNulo : null;
                    if (referencia is not null
                        && (ReferenceEquals(ti.SinNulo, referencia) || ReferenceEquals(ti, TipoLince.Nulo))
                        && (ReferenceEquals(td.SinNulo, referencia) || ReferenceEquals(td, TipoLince.Nulo)))
                    {
                        return new BinariaEnlazada(izquierda, op, derecha, TipoLince.Logico);
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

    private static bool EsReferenciaComparable(TipoLince tipo) => tipo is TipoUsuario { EsEstructura: false } or TipoGenerico;

    private static TipoLince TipoNumericoComun(TipoLince a, TipoLince b) =>
        ReferenceEquals(a, TipoLince.Decimal) || ReferenceEquals(b, TipoLince.Decimal) ? TipoLince.Decimal : TipoLince.Entero;

    private static ExpresionEnlazada ANumerico(ExpresionEnlazada expresion, TipoLince tipo) =>
        ReferenceEquals(expresion.Tipo, tipo) ? expresion : new ConversionEnlazada(expresion, tipo);

    /// <summary>Envuelve en una conversión a texto lo que no sea ya texto (o texto?). Solo para tipos escribibles.</summary>
    private static ExpresionEnlazada ATexto(ExpresionEnlazada expresion) =>
        expresion.Tipo.EsTextual ? expresion : new ConversionEnlazada(expresion, TipoLince.Texto);

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

        if (objeto.Tipo.EsNulable)
        {
            InformarMiembroDeNulable(objeto, miembro.Objeto);
            return new ErrorEnlazado();
        }

        if (objeto.Tipo is not TipoUsuario tipo)
        {
            return EnlazarLlamadaIntegrada(objeto, miembro.Nombre, argumentos, llamada);
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
        if (objeto.Tipo is not TipoUsuario && !objeto.Tipo.EsError && !objeto.Tipo.EsNulable && !miembro.Nombre.EsFaltante)
        {
            return EnlazarPropiedadIntegrada(objeto, miembro.Nombre);
        }

        var campo = BuscarCampoDe(objeto, miembro.Nombre, miembro.Objeto);
        return campo is null ? new ErrorEnlazado() : AplicarNoNulo(new CampoEnlazado(objeto, campo));
    }

    // ----- Tipos integrados: texto, números, lista y mapa -----

    private ExpresionEnlazada EnlazarPropiedadIntegrada(ExpresionEnlazada objeto, Token nombre)
    {
        var miembro = Integrados.Buscar(objeto.Tipo, nombre.Texto);
        if (miembro is null)
        {
            InformarMiembroIntegradoInexistente(objeto.Tipo, nombre);
            return new ErrorEnlazado();
        }

        if (!miembro.EsPropiedad)
        {
            _diagnosticos.Error(Codigos.NombreNoEsVariable, nombre.Ubicacion,
                $"'{nombre.Texto}' es un método de '{objeto.Tipo}'; para llamarlo escriba .{nombre.Texto}(...)");
            return new ErrorEnlazado();
        }

        return new MiembroIntegradoEnlazado(objeto, miembro, []);
    }

    private ExpresionEnlazada EnlazarLlamadaIntegrada(ExpresionEnlazada objeto, Token nombre, ImmutableArray<ExpresionEnlazada> argumentos, LlamadaSintaxis llamada)
    {
        var miembro = Integrados.Buscar(objeto.Tipo, nombre.Texto);
        if (miembro is null)
        {
            InformarMiembroIntegradoInexistente(objeto.Tipo, nombre);
            return new ErrorEnlazado();
        }

        if (miembro.EsPropiedad)
        {
            _diagnosticos.Error(Codigos.NombreNoEsVariable, nombre.Ubicacion,
                $"'{nombre.Texto}' de '{objeto.Tipo}' no es un método; se lee sin paréntesis: .{nombre.Texto}");
            return new ErrorEnlazado();
        }

        var convertidos = ConvertirArgumentos(nombre.Texto, miembro.Parametros, argumentos, llamada.Argumentos, llamada.Ubicacion);
        return convertidos is null ? new ErrorEnlazado() : new MiembroIntegradoEnlazado(objeto, miembro, convertidos.Value);
    }

    private void InformarMiembroIntegradoInexistente(TipoLince tipo, Token nombre)
    {
        var disponibles = Integrados.MiembrosDe(tipo).Select(m => m.Nombre).ToList();
        var pista = disponibles.Count == 0 ? "" : $"; tiene: {string.Join(", ", disponibles)}";
        _diagnosticos.Error(Codigos.MiembroNoExiste, nombre.Ubicacion,
            $"el tipo '{tipo}' no tiene un miembro llamado '{nombre.Texto}'{pista}");
    }

    private ExpresionEnlazada EnlazarListaLiteral(ListaLiteralSintaxis lista, TipoLince? esperado)
    {
        var tipoEsperado = esperado?.SinNulo as TipoGenerico;
        if (lista.Elementos.Length == 0)
        {
            if (tipoEsperado is { EsLista: true })
            {
                return new ListaLiteralEnlazado(tipoEsperado, []);
            }

            if (esperado is not { EsError: true })
            {
                _diagnosticos.Error(Codigos.ColeccionVaciaSinTipo, lista.Ubicacion,
                    "una lista vacía necesita saber su tipo: declare la variable (sea l: lista<entero> = [];) o use nuevo lista<entero>()");
            }

            return new ErrorEnlazado();
        }

        var elementos = lista.Elementos.Select(e => EnlazarExpresion(e)).ToList();
        var tipoElemento = tipoEsperado is { EsLista: true } ? tipoEsperado.Elemento : TipoDeElemento(elementos, lista.Elementos[0]);
        if (tipoElemento.EsError)
        {
            return new ErrorEnlazado();
        }

        var convertidos = ImmutableArray.CreateBuilder<ExpresionEnlazada>(elementos.Count);
        var hayError = false;
        for (var i = 0; i < elementos.Count; i++)
        {
            var convertido = Convertir(elementos[i], tipoElemento, lista.Elementos[i].Ubicacion,
                origen => $"todos los elementos de la lista deben ser '{tipoElemento}' y este es '{origen}'");
            hayError |= convertido.Tipo.EsError;
            convertidos.Add(convertido);
        }

        return hayError ? new ErrorEnlazado() : new ListaLiteralEnlazado(TipoLince.Lista(tipoElemento), convertidos.MoveToImmutable());
    }

    private ExpresionEnlazada EnlazarMapaLiteral(MapaLiteralSintaxis mapa, TipoLince? esperado)
    {
        var tipoEsperado = esperado?.SinNulo as TipoGenerico;
        if (mapa.Pares.Length == 0)
        {
            if (tipoEsperado is { EsMapa: true })
            {
                return new MapaLiteralEnlazado(tipoEsperado, []);
            }

            if (esperado is not { EsError: true })
            {
                _diagnosticos.Error(Codigos.ColeccionVaciaSinTipo, mapa.Ubicacion,
                    "un mapa vacío necesita saber su tipo: declare la variable (sea m: mapa<texto, entero> = {};) o use nuevo mapa<texto, entero>()");
            }

            return new ErrorEnlazado();
        }

        var claves = mapa.Pares.Select(p => EnlazarExpresion(p.Clave)).ToList();
        var valores = mapa.Pares.Select(p => EnlazarExpresion(p.Valor)).ToList();
        var tipoClave = tipoEsperado is { EsMapa: true } ? tipoEsperado.Argumentos[0] : TipoDeElemento(claves, mapa.Pares[0].Clave);
        var tipoValor = tipoEsperado is { EsMapa: true } ? tipoEsperado.Argumentos[1] : TipoDeElemento(valores, mapa.Pares[0].Valor);
        if (tipoClave.EsError || tipoValor.EsError)
        {
            return new ErrorEnlazado();
        }

        var pares = ImmutableArray.CreateBuilder<ParEnlazado>(claves.Count);
        var hayError = false;
        for (var i = 0; i < claves.Count; i++)
        {
            var clave = Convertir(claves[i], tipoClave, mapa.Pares[i].Clave.Ubicacion,
                origen => $"todas las claves del mapa deben ser '{tipoClave}' y esta es '{origen}'");
            var valor = Convertir(valores[i], tipoValor, mapa.Pares[i].Valor.Ubicacion,
                origen => $"todos los valores del mapa deben ser '{tipoValor}' y este es '{origen}'");
            hayError |= clave.Tipo.EsError || valor.Tipo.EsError;
            pares.Add(new ParEnlazado(clave, valor));
        }

        return hayError ? new ErrorEnlazado() : new MapaLiteralEnlazado(TipoLince.Mapa(tipoClave, tipoValor), pares.MoveToImmutable());
    }

    /// <summary>
    /// El tipo que un literal de colección toma de sus elementos: el del primero, salvo que
    /// todos sean números y alguno decimal (entonces decimal). Nulo y nada no sirven.
    /// </summary>
    private TipoLince TipoDeElemento(List<ExpresionEnlazada> elementos, ExpresionSintaxis sintaxis)
    {
        if (elementos.All(e => e.Tipo.EsNumerico) && elementos.Any(e => ReferenceEquals(e.Tipo, TipoLince.Decimal)))
        {
            return TipoLince.Decimal;
        }

        var primero = elementos[0];
        if (ReferenceEquals(primero.Tipo, TipoLince.Nulo))
        {
            _diagnosticos.Error(Codigos.NoSePuedeInferir, sintaxis.Ubicacion,
                "'nulo' no basta para saber el tipo de la colección; declare la variable con su tipo (lista<texto?>)");
            return TipoLince.Error;
        }

        if (ReferenceEquals(primero.Tipo, TipoLince.Nada))
        {
            _diagnosticos.Error(Codigos.ExpresionSinValor, sintaxis.Ubicacion, "la expresión no produce ningún valor (la función regresa 'nada')");
            return TipoLince.Error;
        }

        return primero.Tipo;
    }

    private ExpresionEnlazada EnlazarIndice(IndiceSintaxis indice)
    {
        var objeto = EnlazarExpresion(indice.Objeto);
        var posicion = EnlazarExpresion(indice.Indice);

        if (objeto.Tipo.EsError || posicion.Tipo.EsError)
        {
            return new ErrorEnlazado();
        }

        if (objeto.Tipo.EsNulable)
        {
            InformarMiembroDeNulable(objeto, indice.Objeto);
            return new ErrorEnlazado();
        }

        if (objeto.Tipo is not TipoGenerico coleccion)
        {
            var pista = objeto.Tipo.EsTextual ? "; para un trozo de texto use .subtexto(inicio, longitud)" : "";
            _diagnosticos.Error(Codigos.NoSePuedeIndexar, indice.CorcheteAbre.Ubicacion,
                $"no se puede indexar un valor de tipo '{objeto.Tipo}'; solo las listas y los mapas llevan [ ]{pista}");
            return new ErrorEnlazado();
        }

        var que = coleccion.EsLista ? "el índice de una lista" : "la clave del mapa";
        posicion = Convertir(posicion, coleccion.Indice, indice.Indice.Ubicacion,
            origen => $"{que} debe ser '{coleccion.Indice}' y este es '{origen}'");
        return posicion.Tipo.EsError ? new ErrorEnlazado() : new IndiceEnlazado(objeto, posicion, coleccion.Elemento);
    }

    private void InformarMiembroDeNulable(ExpresionEnlazada objeto, ExpresionSintaxis sintaxis)
    {
        var que = sintaxis.Ubicacion.Texto;
        _diagnosticos.Error(Codigos.MiembroDeNulable, sintaxis.Ubicacion,
            $"'{que}' es de tipo '{objeto.Tipo}' y puede ser nulo; compruébelo antes de usar sus miembros: si {que} != nulo {{ ... }}");
    }

    /// <summary>El campo <c>nombre</c> del tipo del objeto, con sus comprobaciones; null (ya informado) si no procede.</summary>
    private SimboloCampo? BuscarCampoDe(ExpresionEnlazada objeto, Token nombre, ExpresionSintaxis sintaxisDelObjeto)
    {
        if (objeto.Tipo.EsError || nombre.EsFaltante)
        {
            return null;
        }

        if (objeto.Tipo.EsNulable)
        {
            InformarMiembroDeNulable(objeto, sintaxisDelObjeto);
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
        if (nuevo.Tipo.Palabra.EsFaltante)
        {
            return new ErrorEnlazado();
        }

        if (nuevo.Tipo.EsNulable)
        {
            _diagnosticos.Error(Codigos.NuevoDeTipoBasico, nuevo.Tipo.Interrogacion!.Ubicacion,
                "lo que crea 'nuevo' nunca es nulo; quite el '?'");
            return new ErrorEnlazado();
        }

        var tipo = ResolverTipo(nuevo.Tipo);
        switch (tipo)
        {
            case TipoGenerico coleccion:
                if (argumentos.Length > 0)
                {
                    _diagnosticos.Error(Codigos.CantidadDeArgumentos, nuevo.Ubicacion,
                        $"'nuevo {coleccion}()' no recibe argumentos; para crearla con elementos use un literal: [1, 2] o {{\"a\": 1}}");
                    return new ErrorEnlazado();
                }

                return new NuevaColeccionEnlazada(coleccion);

            case TipoUsuario usuario:
                var convertidos = ConvertirArgumentos($"nuevo {usuario.Nombre}", usuario.ParametrosDeNuevo, argumentos, nuevo.Argumentos, nuevo.Ubicacion);
                return convertidos is null ? new ErrorEnlazado() : new NuevoEnlazado(usuario, convertidos.Value);

            case { EsError: true }:
                return new ErrorEnlazado();

            default:
                _diagnosticos.Error(Codigos.NuevoDeTipoBasico, nuevo.Tipo.Ubicacion,
                    $"'{tipo}' es un tipo básico y no se crea con 'nuevo'; escriba el valor directamente");
                return new ErrorEnlazado();
        }
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
            if (destino.EsNulable)
            {
                return new ConversionEnlazada(expresion, destino);
            }

            var pista = destino.EsReferencia ? $"; declárelo como '{destino}?' si puede ser nulo" : "";
            _diagnosticos.Error(Codigos.NuloEnTipoNoNulable, ubicacion,
                $"'nulo' no es un valor válido para el tipo '{destino}'{pista}");
            return new ErrorEnlazado();
        }

        // T → T?: siempre se puede.
        if (destino is TipoNulable nulable && ReferenceEquals(origen, nulable.Base))
        {
            return new ConversionEnlazada(expresion, destino);
        }

        // T? → T: solo donde el flujo ya lo convirtió; aquí es un error con la pista de cómo comprobarlo.
        if (origen is TipoNulable origenNulable && ReferenceEquals(origenNulable.Base, destino))
        {
            var que = Ruta(expresion) is not null ? "este valor" : "esta expresión";
            _diagnosticos.Error(Codigos.PuedeSerNulo, ubicacion,
                $"{que} es de tipo '{origen}' y puede ser nulo, y aquí se necesita '{destino}'; compruébelo antes con si ... != nulo");
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
