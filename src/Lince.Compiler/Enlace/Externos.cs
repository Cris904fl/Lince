using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lince.Compiler.Enlace;

/// <summary>
/// La puerta a .NET: busca tipos por nombre en los espacios importados, traduce tipos de .NET
/// a tipos de Lince y resuelve sobrecargas. Hoy mira el runtime con el que corre el compilador
/// (el mismo contra el que se emite); los ensamblados de referencia y los paquetes NuGet
/// llegan en la entrega 4b.
/// </summary>
public static class Externos
{
    private static readonly object _candado = new();
    private static List<Assembly>? _ensamblados;
    private static HashSet<string>? _espacios;
    private static readonly Dictionary<string, Type?> _tiposPorNombre = new(StringComparer.Ordinal);

    /// <summary>Los ensamblados del runtime (System.* y Microsoft.*) donde se buscan los tipos; se cargan una vez.</summary>
    private static List<Assembly> Ensamblados
    {
        get
        {
            lock (_candado)
            {
                if (_ensamblados is not null)
                {
                    return _ensamblados;
                }

                var cargados = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && EsDelRuntime(a.GetName().Name))
                    .ToDictionary(a => a.GetName().Name!, a => a);

                foreach (var ruta in Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
                {
                    var nombre = Path.GetFileNameWithoutExtension(ruta);
                    if (!EsDelRuntime(nombre) || cargados.ContainsKey(nombre) || nombre.Contains("Native", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        cargados[nombre] = Assembly.Load(new AssemblyName(nombre));
                    }
                    catch (Exception excepcion) when (excepcion is BadImageFormatException or FileLoadException or FileNotFoundException)
                    {
                        // Ensamblados nativos o no cargables: no tienen tipos que ofrecer.
                    }
                }

                _ensamblados = cargados.Values.ToList();
                return _ensamblados;
            }
        }
    }

    private static bool EsDelRuntime(string? nombre) =>
        nombre is not null && (nombre.StartsWith("System", StringComparison.Ordinal) || nombre.StartsWith("Microsoft.", StringComparison.Ordinal)
            || nombre is "mscorlib" or "netstandard");

    /// <summary>Si algún ensamblado del runtime tiene tipos públicos en ese espacio de nombres (o en uno anidado en él).</summary>
    public static bool ExisteEspacio(string espacio)
    {
        lock (_candado)
        {
            if (_espacios is null)
            {
                _espacios = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ensamblado in Ensamblados)
                {
                    Type[] tipos;
                    try
                    {
                        tipos = ensamblado.GetExportedTypes();
                    }
                    catch (Exception excepcion) when (excepcion is ReflectionTypeLoadException or FileNotFoundException or TypeLoadException)
                    {
                        continue;
                    }

                    foreach (var tipo in tipos)
                    {
                        for (var ns = tipo.Namespace; !string.IsNullOrEmpty(ns); ns = ns.Contains('.') ? ns[..ns.LastIndexOf('.')] : null)
                        {
                            _espacios.Add(ns);
                        }
                    }
                }
            }

            return _espacios.Contains(espacio);
        }
    }

    /// <summary>Un tipo público de .NET por su nombre simple, mirando en cada espacio importado en orden.</summary>
    public static Type? BuscarTipo(string nombre, IEnumerable<string> espacios)
    {
        foreach (var espacio in espacios)
        {
            var completo = espacio + "." + nombre;
            if (BuscarPorNombreCompleto(completo) is { } tipo)
            {
                return tipo;
            }
        }

        return null;
    }

    private static Type? BuscarPorNombreCompleto(string completo)
    {
        lock (_candado)
        {
            if (_tiposPorNombre.TryGetValue(completo, out var conocido))
            {
                return conocido;
            }
        }

        Type? encontrado = null;
        foreach (var ensamblado in Ensamblados)
        {
            Type? tipo;
            try
            {
                tipo = ensamblado.GetType(completo, throwOnError: false);
            }
            catch (Exception excepcion) when (excepcion is FileNotFoundException or TypeLoadException or BadImageFormatException)
            {
                continue;
            }

            if (tipo is { IsPublic: true, IsGenericTypeDefinition: false })
            {
                encontrado = tipo;
                break;
            }
        }

        lock (_candado)
        {
            _tiposPorNombre[completo] = encontrado;
        }

        return encontrado;
    }

    // ----- Traducción de tipos -----

    /// <summary>
    /// El tipo de Lince con el que se ve un tipo de .NET: los básicos y las colecciones se
    /// traducen; los enteros cortos y <c>float</c> se ven como <c>entero</c> y <c>decimal</c>
    /// (el emisor convierte en la frontera); lo demás queda como tipo externo. Null si no se
    /// puede usar desde Lince (parámetros genéricos, referencias, punteros).
    /// </summary>
    public static TipoLince? ALince(Type tipo)
    {
        if (tipo.IsByRef || tipo.IsPointer || tipo.IsGenericParameter || tipo.ContainsGenericParameters)
        {
            return null;
        }

        if (tipo == typeof(void))
        {
            return TipoLince.Nada;
        }

        if (tipo == typeof(long) || tipo == typeof(int) || tipo == typeof(short) || tipo == typeof(byte)
            || tipo == typeof(sbyte) || tipo == typeof(ushort) || tipo == typeof(uint))
        {
            return TipoLince.Entero;
        }

        if (tipo == typeof(double) || tipo == typeof(float))
        {
            return TipoLince.Decimal;
        }

        if (tipo == typeof(string) || tipo == typeof(char))
        {
            // Un char de .NET se ve como un texto de un carácter; el emisor convierte en la frontera.
            return TipoLince.Texto;
        }

        if (tipo == typeof(bool))
        {
            return TipoLince.Logico;
        }

        if (tipo.IsGenericType)
        {
            var definicion = tipo.GetGenericTypeDefinition();
            var argumentos = tipo.GetGenericArguments().Select(ALince).ToArray();
            if (argumentos.Any(a => a is null || ReferenceEquals(a, TipoLince.Nada)))
            {
                return TipoLince.Externo(tipo);
            }

            if (definicion == typeof(List<>))
            {
                return TipoLince.Lista(argumentos[0]!);
            }

            if (definicion == typeof(Dictionary<,>))
            {
                return TipoLince.Mapa(argumentos[0]!, argumentos[1]!);
            }

            if (definicion.FullName is { } nombre && nombre.StartsWith("System.Func`", StringComparison.Ordinal))
            {
                return TipoLince.Funcion([.. argumentos.Take(argumentos.Length - 1)!], argumentos[^1]!);
            }

            if (definicion.FullName is { } nombreAccion && nombreAccion.StartsWith("System.Action`", StringComparison.Ordinal))
            {
                return TipoLince.Funcion([.. argumentos!], TipoLince.Nada);
            }
        }

        if (tipo == typeof(Action))
        {
            return TipoLince.Funcion([], TipoLince.Nada);
        }

        // Cualquier otro delegado (Predicate<T>, Comparison<T>, EventHandler...) se ve por la firma de su Invoke;
        // el emisor crea el delegado del tipo exacto que .NET pide.
        if (typeof(Delegate).IsAssignableFrom(tipo) && tipo.GetMethod("Invoke") is { } invoke)
        {
            var parametros = invoke.GetParameters().Select(p => ALince(p.ParameterType)).ToArray();
            var retorno = ALince(invoke.ReturnType);
            if (retorno is not null && parametros.All(p => p is not null && !ReferenceEquals(p, TipoLince.Nada)))
            {
                return TipoLince.Funcion([.. parametros!], retorno);
            }
        }

        return TipoLince.Externo(tipo);
    }

    /// <summary>Si un parámetro de .NET es un delegado distinto de Func/Action, el tipo exacto que hay que construir.</summary>
    public static Type? DelegadoExacto(Type parametro) =>
        typeof(Delegate).IsAssignableFrom(parametro)
        && !(parametro.FullName?.StartsWith("System.Func`", StringComparison.Ordinal) ?? false)
        && !(parametro.FullName?.StartsWith("System.Action", StringComparison.Ordinal) ?? false)
            ? parametro
            : null;

    // ----- Sobrecargas -----

    /// <summary>Qué tan bien encaja un argumento de Lince en un parámetro de .NET; null si no encaja.</summary>
    public static int? Compatibilidad(TipoLince argumento, Type parametro, Func<TipoLince, Type?> clr)
    {
        if (argumento.EsError)
        {
            return 1;
        }

        if (ReferenceEquals(argumento, TipoLince.Nulo))
        {
            return parametro.IsValueType ? null : 2;
        }

        if (ReferenceEquals(argumento, TipoLince.NombreDeTipo) || ReferenceEquals(argumento, TipoLince.Nada))
        {
            return null;
        }

        var sinNulo = argumento.SinNulo;
        if (ReferenceEquals(sinNulo, TipoLince.Entero))
        {
            if (parametro == typeof(long)) return 4;
            if (parametro == typeof(double)) return 3;
            if (parametro == typeof(int) || parametro == typeof(short) || parametro == typeof(byte) || parametro == typeof(sbyte)
                || parametro == typeof(ushort) || parametro == typeof(uint) || parametro == typeof(float))
            {
                return 2;
            }
        }

        if (ReferenceEquals(sinNulo, TipoLince.Decimal))
        {
            if (parametro == typeof(double)) return 4;
            if (parametro == typeof(float)) return 2;
        }

        if (ReferenceEquals(sinNulo, TipoLince.Texto) && parametro == typeof(string)) return 4;
        if (ReferenceEquals(sinNulo, TipoLince.Texto) && parametro == typeof(char)) return 2;
        if (ReferenceEquals(sinNulo, TipoLince.Logico) && parametro == typeof(bool)) return 4;

        if (parametro == typeof(object))
        {
            return 1;
        }

        var clrArgumento = clr(sinNulo);
        if (clrArgumento is null)
        {
            return null;
        }

        if (clrArgumento == parametro)
        {
            return 4;
        }

        // Un tipo en construcción (clase de Lince) no se puede comprobar; solo encaja en object (ya visto).
        if (clrArgumento is System.Reflection.Emit.TypeBuilder || clrArgumento.IsGenericType && clrArgumento.GetGenericArguments().Any(a => a is System.Reflection.Emit.TypeBuilder))
        {
            return null;
        }

        return parametro.IsAssignableFrom(clrArgumento) ? 3 : null;
    }

    /// <summary>Si un parámetro de .NET es un delegado que Lince puede ver como tipo función.</summary>
    public static TipoFuncion? ComoFuncion(Type parametro) => ALince(parametro) as TipoFuncion;

    /// <summary>
    /// El tipo de .NET de un tipo de Lince ya existente en el runtime: básicos, externos,
    /// colecciones y funciones sin tipos de usuario dentro. Null si depende de una clase de
    /// Lince (esas las construye el emisor) o si es nada.
    /// </summary>
    public static Type? ClrDe(TipoLince tipo)
    {
        switch (tipo)
        {
            case TipoUsuario:
                return null;
            case TipoNulable nulable:
                return ClrDe(nulable.Base);
            case TipoExterno externo:
                return externo.TipoClr;
            case TipoGenerico generico:
                {
                    var argumentos = generico.Argumentos.Select(ClrDe).ToArray();
                    return argumentos.Any(a => a is null) ? null
                        : (generico.EsLista ? typeof(List<>) : typeof(Dictionary<,>)).MakeGenericType(argumentos!);
                }

            case TipoFuncion funcion:
                {
                    var argumentos = funcion.Parametros.Select(ClrDe).ToList();
                    if (!funcion.RegresaNada)
                    {
                        argumentos.Add(ClrDe(funcion.Retorno));
                    }

                    if (argumentos.Any(a => a is null))
                    {
                        return null;
                    }

                    var n = funcion.Parametros.Length;
                    if (funcion.RegresaNada)
                    {
                        return n == 0 ? typeof(Action) : Type.GetType($"System.Action`{n}", throwOnError: true)!.MakeGenericType(argumentos!.ToArray()!);
                    }

                    return Type.GetType($"System.Func`{n + 1}", throwOnError: true)!.MakeGenericType(argumentos!.ToArray()!);
                }

            default:
                return ReferenceEquals(tipo, TipoLince.Nada) || ReferenceEquals(tipo, TipoLince.Nulo)
                    || ReferenceEquals(tipo, TipoLince.Error) || ReferenceEquals(tipo, TipoLince.NombreDeTipo)
                    ? null
                    : tipo.TipoClr;
        }
    }
}
