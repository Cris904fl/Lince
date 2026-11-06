using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Lince.Compiler.Enlace;

namespace Lince.Compiler.Emision;

/// <summary>
/// Convierte el programa enlazado en un ensamblado .NET ejecutable con
/// <see cref="PersistedAssemblyBuilder"/>. Las funciones libres son métodos estáticos de una
/// clase <c>Programa</c>; cada clase o estructura de Lince es un tipo de .NET con el mismo
/// nombre. Las lambdas son métodos aparte: estáticos si no capturan nada, o métodos de una
/// clase de cierre anidada (una por función) que guarda las variables capturadas, como hace
/// C#. El punto de entrada es un <c>Main</c> generado que llama a <c>principal</c> y convierte
/// su resultado en código de salida.
/// </summary>
public static class EmisorIl
{
    public const string NombreDeClase = "Programa";
    public const string NombreDeEntrada = "Main";

    public static byte[] Emitir(ProgramaEnlazado programa, string nombreEnsamblado)
    {
        if (programa.Principal is null)
        {
            throw new InvalidOperationException("No se puede emitir un programa sin función principal.");
        }

        var ensamblado = new PersistedAssemblyBuilder(new AssemblyName(nombreEnsamblado), typeof(object).Assembly);
        var modulo = ensamblado.DefineDynamicModule(nombreEnsamblado);
        var tabla = new Tabla();

        // 1. Los tipos, para que cualquier firma pueda nombrarlos.
        foreach (var tipo in programa.Tipos)
        {
            var atributos = TypeAttributes.Public | TypeAttributes.Class;
            var constructor = tipo.Tipo.EsEstructura
                ? modulo.DefineType(tipo.Tipo.Nombre, atributos | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType))
                : modulo.DefineType(tipo.Tipo.Nombre, atributos);
            tabla.Tipos[tipo.Tipo] = constructor;
        }

        var programaClase = modulo.DefineType(NombreDeClase, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);

        // 2. Campos, firmas de métodos y constructores.
        foreach (var tipo in programa.Tipos)
        {
            var constructor = tabla.Tipos[tipo.Tipo];
            foreach (var campo in tipo.Tipo.Campos)
            {
                var atributos = campo.EsPrivado ? FieldAttributes.Private : FieldAttributes.Public;
                tabla.Campos[campo] = constructor.DefineField(campo.Nombre, tabla.Clr(campo.Tipo), atributos);
            }

            foreach (var metodo in tipo.Metodos)
            {
                DefinirMetodo(constructor, metodo.Simbolo, tabla);
            }

            var parametros = tipo.Tipo.ParametrosDeNuevo.Select(p => tabla.Clr(p.Tipo)).ToArray();
            var ctor = constructor.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, CallingConventions.Standard, parametros);
            for (var i = 0; i < tipo.Tipo.ParametrosDeNuevo.Length; i++)
            {
                ctor.DefineParameter(i + 1, ParameterAttributes.None, tipo.Tipo.ParametrosDeNuevo[i].Nombre);
            }

            tabla.Constructores[tipo.Tipo] = ctor;
        }

        foreach (var funcion in programa.Funciones)
        {
            DefinirMetodo(programaClase, funcion.Simbolo, tabla);
        }

        // 3. Clases de cierre y métodos de las lambdas, antes de cualquier cuerpo: crear un
        //    delegado necesita el método ya definido.
        foreach (var tipo in programa.Tipos)
        {
            var contenedor = tabla.Tipos[tipo.Tipo];
            foreach (var metodo in tipo.Metodos)
            {
                PrepararLambdas(metodo, contenedor, tipo.Tipo, tabla);
            }

            if (tipo.Constructor is not null)
            {
                PrepararLambdas(tipo.Constructor, contenedor, tipo.Tipo, tabla);
            }
        }

        foreach (var funcion in programa.Funciones)
        {
            PrepararLambdas(funcion, programaClase, null, tabla);
        }

        // 4. Cuerpos.
        foreach (var tipo in programa.Tipos)
        {
            EmitirConstructor(tipo, tabla);
            foreach (var metodo in tipo.Metodos)
            {
                EmitirFuncion(metodo, tabla.Metodos[metodo.Simbolo].GetILGenerator(), tabla);
            }
        }

        foreach (var funcion in programa.Funciones)
        {
            EmitirFuncion(funcion, tabla.Metodos[funcion.Simbolo].GetILGenerator(), tabla);
        }

        var entrada = EmitirEntrada(programaClase, tabla.Metodos[programa.Principal], programa.Principal);

        // 5. Crear los tipos: los cierres antes que quien los contiene; las estructuras antes
        //    que las clases, porque una clase puede tener un campo de tipo estructura.
        foreach (var cierre in tabla.Cierres)
        {
            cierre.CreateType();
        }

        foreach (var tipo in programa.Tipos.OrderByDescending(t => t.Tipo.EsEstructura))
        {
            tabla.Tipos[tipo.Tipo].CreateType();
        }

        programaClase.CreateType();

        var metadatos = ensamblado.GenerateMetadata(out var il, out var datosDeCampos);
        var pe = new ManagedPEBuilder(
            PEHeaderBuilder.CreateExecutableHeader(),
            new MetadataRootBuilder(metadatos),
            il,
            mappedFieldData: datosDeCampos,
            entryPoint: MetadataTokens.MethodDefinitionHandle(entrada.MetadataToken));

        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }

    private static void DefinirMetodo(TypeBuilder clase, SimboloFuncion simbolo, Tabla tabla)
    {
        var atributos = (simbolo.EsPrivada ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig;
        if (!simbolo.EsMetodo)
        {
            atributos |= MethodAttributes.Static;
        }

        var metodo = clase.DefineMethod(
            simbolo.Nombre,
            atributos,
            tabla.Clr(simbolo.TipoRetorno),
            simbolo.Parametros.Select(p => tabla.Clr(p.Tipo)).ToArray());

        for (var i = 0; i < simbolo.Parametros.Length; i++)
        {
            metodo.DefineParameter(i + 1, ParameterAttributes.None, simbolo.Parametros[i].Nombre);
        }

        tabla.Metodos[simbolo] = metodo;
    }

    /// <summary>
    /// Define lo que las lambdas de un cuerpo necesitan: si capturan algo, una clase de cierre
    /// anidada en el tipo contenedor (así ve sus miembros privados) con un campo por variable
    /// capturada y, si hace falta, uno para <c>este</c>; y un método por lambda, de instancia
    /// en el cierre o estático en el contenedor.
    /// </summary>
    private static void PrepararLambdas(FuncionEnlazada funcion, TypeBuilder contenedor, TipoUsuario? tipoContenedor, Tabla tabla)
    {
        if (funcion.Lambdas is null)
        {
            return;
        }

        var contexto = new ContextoDeLambdas();
        if (funcion.Lambdas.NecesitaCierre)
        {
            var cierre = contenedor.DefineNestedType($"<Cierre>{funcion.Simbolo.Nombre}",
                TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Class);
            contexto.Cierre = cierre;
            contexto.ConstructorDeCierre = cierre.DefineDefaultConstructor(MethodAttributes.Public);
            foreach (var variable in funcion.Lambdas.Capturadas)
            {
                contexto.Capturadas[variable] = cierre.DefineField(variable.Nombre, tabla.Clr(variable.Tipo), FieldAttributes.Public);
            }

            if (funcion.Lambdas.CapturaEste)
            {
                contexto.CampoEste = cierre.DefineField("<este>", tabla.Clr(tipoContenedor!), FieldAttributes.Public);
            }

            tabla.Cierres.Add(cierre);
        }

        foreach (var lambda in funcion.Lambdas.Lambdas)
        {
            var nombre = funcion.Simbolo.Nombre + lambda.Simbolo.Nombre;
            var parametros = lambda.Simbolo.Parametros.Select(p => tabla.Clr(p.Tipo)).ToArray();
            var retorno = tabla.Clr(lambda.Simbolo.TipoRetorno);
            var metodo = contexto.Cierre is not null
                ? contexto.Cierre.DefineMethod(nombre, MethodAttributes.Public | MethodAttributes.HideBySig, retorno, parametros)
                : contenedor.DefineMethod(nombre, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, retorno, parametros);
            for (var i = 0; i < lambda.Simbolo.Parametros.Length; i++)
            {
                metodo.DefineParameter(i + 1, ParameterAttributes.None, lambda.Simbolo.Parametros[i].Nombre);
            }

            contexto.Metodos[lambda.Simbolo] = metodo;
        }

        tabla.Lambdas[funcion.Simbolo] = contexto;
    }

    /// <summary>Emite el cuerpo de una función y, después, los de sus lambdas.</summary>
    private static void EmitirFuncion(FuncionEnlazada funcion, ILGenerator il, Tabla tabla)
    {
        var contexto = tabla.Lambdas.GetValueOrDefault(funcion.Simbolo);
        new EmisorDeFuncion(il, funcion.Simbolo, tabla, contexto, esLambda: false).EmitirCuerpo(funcion.Cuerpo);
        EmitirLambdasDe(funcion, tabla);
    }

    private static void EmitirLambdasDe(FuncionEnlazada funcion, Tabla tabla)
    {
        if (funcion.Lambdas is null)
        {
            return;
        }

        var contexto = tabla.Lambdas[funcion.Simbolo];
        foreach (var lambda in funcion.Lambdas.Lambdas)
        {
            var il = contexto.Metodos[lambda.Simbolo].GetILGenerator();
            new EmisorDeFuncion(il, lambda.Simbolo, tabla, contexto, esLambda: true).EmitirCuerpo(lambda.Cuerpo);
        }
    }

    /// <summary>
    /// El constructor: inicializa el objeto (llamando al de <c>object</c> o poniendo a cero la
    /// estructura), asigna los valores iniciales de los campos y después corre el cuerpo de
    /// <c>funcion nuevo</c> o, si no lo hay, copia cada argumento en su campo.
    /// </summary>
    private static void EmitirConstructor(TipoEnlazado tipo, Tabla tabla)
    {
        var ctor = tabla.Constructores[tipo.Tipo];
        var il = ctor.GetILGenerator();

        if (tipo.Tipo.EsEstructura)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Initobj, tabla.Tipos[tipo.Tipo]);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Clr.ConstructorDeObjeto);
        }

        var simbolo = tipo.Constructor?.Simbolo
            ?? new SimboloFuncion(Enlazador.NombreDeConstructor, tipo.Tipo.ParametrosDeNuevo, TipoLince.Nada, null, tipo.Tipo);
        var contexto = tipo.Constructor is null ? null : tabla.Lambdas.GetValueOrDefault(tipo.Constructor.Simbolo);
        var emisor = new EmisorDeFuncion(il, simbolo, tabla, contexto, esLambda: false);

        foreach (var inicial in tipo.Iniciales)
        {
            il.Emit(OpCodes.Ldarg_0);
            emisor.EmitirExpresion(inicial.Valor);
            il.Emit(OpCodes.Stfld, tabla.Campos[inicial.Campo]);
        }

        if (tipo.Constructor is not null)
        {
            emisor.EmitirCuerpo(tipo.Constructor.Cuerpo);
            EmitirLambdasDe(tipo.Constructor, tabla);
            return;
        }

        var camposSinInicial = tipo.Tipo.Campos.Where(c => c.Declaracion.Inicial is null).ToList();
        for (var i = 0; i < camposSinInicial.Count; i++)
        {
            il.Emit(OpCodes.Ldarg_0);
            EmisorDeFuncion.EmitirCargaDeArgumento(il, i + 1);
            il.Emit(OpCodes.Stfld, tabla.Campos[camposSinInicial[i]]);
        }

        il.Emit(OpCodes.Ret);
    }

    private static MethodBuilder EmitirEntrada(TypeBuilder clase, MethodBuilder principal, SimboloFuncion simboloPrincipal)
    {
        var entrada = clase.DefineMethod(NombreDeEntrada, MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = entrada.GetILGenerator();

        // La consola en UTF-8, para que las tildes y la eñe salgan bien en cualquier terminal.
        il.Emit(OpCodes.Call, Clr.ObtenerUtf8);
        il.Emit(OpCodes.Call, Clr.FijarCodificacionDeSalida);

        il.Emit(OpCodes.Call, principal);
        if (ReferenceEquals(simboloPrincipal.TipoRetorno, TipoLince.Entero))
        {
            il.Emit(OpCodes.Conv_I4);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
        }

        il.Emit(OpCodes.Ret);
        return entrada;
    }

    /// <summary>Lo que el emisor definió para las lambdas de una función.</summary>
    private sealed class ContextoDeLambdas
    {
        public TypeBuilder? Cierre { get; set; }

        public ConstructorBuilder? ConstructorDeCierre { get; set; }

        public Dictionary<SimboloVariable, FieldBuilder> Capturadas { get; } = new();

        public FieldBuilder? CampoEste { get; set; }

        public Dictionary<SimboloFuncion, MethodBuilder> Metodos { get; } = new();
    }

    /// <summary>Lo que el emisor ya definió, para que los cuerpos puedan referirse a ello.</summary>
    private sealed class Tabla
    {
        private readonly Dictionary<TipoLince, Type> _construidos = new();

        public Dictionary<TipoUsuario, TypeBuilder> Tipos { get; } = new();

        public Dictionary<SimboloCampo, FieldBuilder> Campos { get; } = new();

        public Dictionary<SimboloFuncion, MethodBuilder> Metodos { get; } = new();

        public Dictionary<TipoUsuario, ConstructorBuilder> Constructores { get; } = new();

        public Dictionary<SimboloFuncion, ContextoDeLambdas> Lambdas { get; } = new();

        public List<TypeBuilder> Cierres { get; } = new();

        /// <summary>
        /// El tipo de .NET de un tipo de Lince: fijo para los básicos, el TypeBuilder para los de
        /// usuario, <c>List&lt;&gt;</c> o <c>Dictionary&lt;,&gt;</c> construidos para las colecciones,
        /// <c>Func&lt;&gt;</c> o <c>Action&lt;&gt;</c> para las funciones; un T? es el mismo que T.
        /// </summary>
        public Type Clr(TipoLince tipo)
        {
            var sinNulo = tipo.SinNulo;
            switch (sinNulo)
            {
                case TipoUsuario usuario:
                    return Tipos[usuario];
                case TipoGenerico or TipoFuncion:
                    if (!_construidos.TryGetValue(sinNulo, out var construido))
                    {
                        construido = sinNulo is TipoGenerico generico
                            ? (generico.EsLista ? typeof(List<>) : typeof(Dictionary<,>)).MakeGenericType(generico.Argumentos.Select(Clr).ToArray())
                            : ConstruirDelegado((TipoFuncion)sinNulo);
                        _construidos[sinNulo] = construido;
                    }

                    return construido;
                default:
                    return sinNulo.TipoClr;
            }
        }

        private Type ConstruirDelegado(TipoFuncion funcion)
        {
            var abierto = DelegadoAbierto(funcion);
            if (!abierto.IsGenericTypeDefinition)
            {
                return abierto;
            }

            var argumentos = funcion.Parametros.Select(Clr).ToList();
            if (!funcion.RegresaNada)
            {
                argumentos.Add(Clr(funcion.Retorno));
            }

            return abierto.MakeGenericType(argumentos.ToArray());
        }

        /// <summary><c>Action</c>, <c>Action&lt;...&gt;</c> o <c>Func&lt;...&gt;</c> abierto según la firma.</summary>
        public static Type DelegadoAbierto(TipoFuncion funcion)
        {
            var n = funcion.Parametros.Length;
            if (funcion.RegresaNada)
            {
                return n == 0 ? typeof(Action) : Type.GetType($"System.Action`{n}", throwOnError: true)!;
            }

            return Type.GetType($"System.Func`{n + 1}", throwOnError: true)!;
        }

        public ConstructorInfo ConstructorDeDelegado(TipoFuncion funcion)
        {
            var abierto = DelegadoAbierto(funcion).GetConstructor([typeof(object), typeof(IntPtr)])!;
            return abierto.DeclaringType!.IsGenericTypeDefinition ? Constructor(Clr(funcion), abierto) : abierto;
        }

        public MethodInfo InvocarDelegado(TipoFuncion funcion) =>
            Metodo(Clr(funcion), DelegadoAbierto(funcion).GetMethod("Invoke")!);

        /// <summary>
        /// Un método de un tipo genérico abierto (<c>List&lt;T&gt;.Add</c>) sobre su instancia
        /// construida. Si la instancia incluye un tipo que aún se está construyendo hay que
        /// pasar por <see cref="TypeBuilder.GetMethod(Type, MethodInfo)"/>; si no, por el handle.
        /// Los métodos de tipos no genéricos (<c>String</c>, <c>Math</c>) se usan tal cual.
        /// </summary>
        public MethodInfo Metodo(Type construido, MethodInfo abierto)
        {
            if (!abierto.DeclaringType!.IsGenericTypeDefinition)
            {
                return abierto;
            }

            return TieneBuilder(construido)
                ? TypeBuilder.GetMethod(construido, abierto)
                : (MethodInfo)MethodBase.GetMethodFromHandle(abierto.MethodHandle, construido.TypeHandle)!;
        }

        public ConstructorInfo Constructor(Type construido, ConstructorInfo abierto) => TieneBuilder(construido)
            ? TypeBuilder.GetConstructor(construido, abierto)
            : (ConstructorInfo)MethodBase.GetMethodFromHandle(abierto.MethodHandle, construido.TypeHandle)!;

        public static bool TieneBuilder(Type tipo) =>
            tipo is TypeBuilder || (tipo.IsGenericType && !tipo.IsGenericTypeDefinition && tipo.GetGenericArguments().Any(TieneBuilder));
    }

    /// <summary>Miembros abiertos de las colecciones de .NET que emite el código generado.</summary>
    private static class Colecciones
    {
        private static readonly Type T = typeof(List<>).GetGenericArguments()[0];
        private static readonly Type K = typeof(Dictionary<,>).GetGenericArguments()[0];
        private static readonly Type V = typeof(Dictionary<,>).GetGenericArguments()[1];

        public static readonly ConstructorInfo ListaVacia = typeof(List<>).GetConstructor(Type.EmptyTypes)!;
        public static readonly ConstructorInfo ListaDesde = typeof(List<>).GetConstructor([typeof(IEnumerable<>).MakeGenericType(T)])!;
        public static readonly MethodInfo ListaAgregar = typeof(List<>).GetMethod(nameof(List<int>.Add), [T])!;
        public static readonly MethodInfo ListaCantidad = typeof(List<>).GetProperty(nameof(List<int>.Count))!.GetMethod!;
        public static readonly MethodInfo ListaLeer = typeof(List<>).GetMethod("get_Item", [typeof(int)])!;
        public static readonly MethodInfo ListaEscribir = typeof(List<>).GetMethod("set_Item", [typeof(int), T])!;

        public static readonly ConstructorInfo MapaVacio = typeof(Dictionary<,>).GetConstructor(Type.EmptyTypes)!;
        public static readonly MethodInfo MapaLeer = typeof(Dictionary<,>).GetMethod("get_Item", [K])!;
        public static readonly MethodInfo MapaEscribir = typeof(Dictionary<,>).GetMethod("set_Item", [K, V])!;
    }

    /// <summary>Miembros de .NET que el código generado llama. Se resuelven una vez.</summary>
    private static class Clr
    {
        public static readonly MethodInfo EscribirLinea = Metodo(typeof(Console), nameof(Console.WriteLine), typeof(string));
        public static readonly MethodInfo EscribirLineaVacia = Metodo(typeof(Console), nameof(Console.WriteLine));
        public static readonly MethodInfo Concatenar = Metodo(typeof(string), nameof(string.Concat), typeof(string), typeof(string));
        public static readonly MethodInfo ConcatenarObjeto = Metodo(typeof(string), nameof(string.Concat), typeof(object));
        public static readonly MethodInfo CaracterEn = typeof(string).GetProperty("Chars")!.GetMethod!;
        public static readonly MethodInfo CaracterATexto = Metodo(typeof(char), nameof(char.ToString), typeof(char));
        public static readonly MethodInfo TextosIguales = Metodo(typeof(string), nameof(string.Equals), typeof(string), typeof(string));
        public static readonly MethodInfo CulturaInvariante = typeof(CultureInfo).GetProperty(nameof(CultureInfo.InvariantCulture))!.GetMethod!;
        public static readonly MethodInfo EnteroATexto = Metodo(typeof(long), nameof(long.ToString), typeof(IFormatProvider));
        public static readonly MethodInfo DecimalATexto = Metodo(typeof(double), nameof(double.ToString), typeof(IFormatProvider));
        public static readonly MethodInfo ObtenerUtf8 = typeof(Encoding).GetProperty(nameof(Encoding.UTF8))!.GetMethod!;
        public static readonly MethodInfo FijarCodificacionDeSalida = typeof(Console).GetProperty(nameof(Console.OutputEncoding))!.SetMethod!;
        public static readonly ConstructorInfo ConstructorDeObjeto = typeof(object).GetConstructor(Type.EmptyTypes)!;

        private static MethodInfo Metodo(Type tipo, string nombre, params Type[] parametros) =>
            tipo.GetMethod(nombre, parametros) ?? throw new MissingMethodException(tipo.FullName, nombre);
    }

    /// <summary>
    /// Emite el cuerpo de una función, método, constructor o lambda. Una instancia por cuerpo:
    /// guarda sus locales y sus ciclos. Las variables capturadas por lambdas no son locales
    /// sino campos del cierre: en la función se llega a él por una local, en la lambda es <c>este</c>.
    /// </summary>
    private sealed class EmisorDeFuncion
    {
        private readonly ILGenerator _il;
        private readonly SimboloFuncion _funcion;
        private readonly Tabla _tabla;
        private readonly ContextoDeLambdas? _contexto;
        private readonly bool _esLambda;
        private readonly Dictionary<SimboloVariable, LocalBuilder> _locales = new();
        private readonly Stack<(Label Romper, Label Continuar)> _ciclos = new();
        private LocalBuilder? _temporalEntero;
        private LocalBuilder? _temporalDecimal;
        private LocalBuilder? _cierre;

        /// <summary>En un método de instancia (o una lambda de un cierre) el argumento 0 es <c>este</c>; los parámetros empiezan en 1.</summary>
        private readonly int _desplazamientoDeArgumentos;

        public EmisorDeFuncion(ILGenerator il, SimboloFuncion funcion, Tabla tabla, ContextoDeLambdas? contexto, bool esLambda)
        {
            _il = il;
            _funcion = funcion;
            _tabla = tabla;
            _contexto = contexto;
            _esLambda = esLambda;
            _desplazamientoDeArgumentos = esLambda ? (contexto?.Cierre is null ? 0 : 1) : (funcion.EsMetodo ? 1 : 0);
        }

        private bool TieneCierre => _contexto?.Cierre is not null;

        public void EmitirCuerpo(BloqueEnlazado cuerpo)
        {
            if (!_esLambda && TieneCierre)
            {
                CrearCierre();
            }
            else if (_esLambda && TieneCierre)
            {
                // Los parámetros de esta lambda que capture otra lambda anidada van al cierre.
                CopiarParametrosCapturados();
            }

            EmitirBloque(cuerpo);

            // El enlazador ya garantizó que las funciones con valor regresan por todos los
            // caminos; este cierre es inalcanzable pero mantiene el IL bien formado.
            var tipoRetorno = _funcion.TipoRetorno;
            if (!ReferenceEquals(tipoRetorno, TipoLince.Nada))
            {
                EmitirValorPorDefecto(tipoRetorno);
            }

            _il.Emit(OpCodes.Ret);
        }

        /// <summary>Al entrar en una función con capturas: crear el cierre y copiarle los parámetros y el 'este' capturados.</summary>
        private void CrearCierre()
        {
            _cierre = _il.DeclareLocal(_contexto!.Cierre!);
            _il.Emit(OpCodes.Newobj, _contexto.ConstructorDeCierre!);
            _il.Emit(OpCodes.Stloc, _cierre);
            CopiarParametrosCapturados();

            if (_contexto.CampoEste is not null)
            {
                _il.Emit(OpCodes.Ldloc, _cierre);
                _il.Emit(OpCodes.Ldarg_0);
                if (_funcion.TipoContenedor!.EsEstructura)
                {
                    _il.Emit(OpCodes.Ldobj, _tabla.Clr(_funcion.TipoContenedor));
                }

                _il.Emit(OpCodes.Stfld, _contexto.CampoEste);
            }
        }

        /// <summary>Los parámetros de ESTE cuerpo que alguna lambda captura se copian al cierre al entrar; después se leen de ahí.</summary>
        private void CopiarParametrosCapturados()
        {
            foreach (var parametro in _funcion.Parametros)
            {
                if (!EsCapturada(parametro))
                {
                    continue;
                }

                EmitirCierre();
                EmitirCargaDeArgumento(_il, parametro.IndiceDeParametro + _desplazamientoDeArgumentos);
                _il.Emit(OpCodes.Stfld, _contexto!.Capturadas[parametro]);
            }
        }

        private void EmitirValorPorDefecto(TipoLince tipo)
        {
            if (ReferenceEquals(tipo, TipoLince.Entero))
            {
                _il.Emit(OpCodes.Ldc_I8, 0L);
            }
            else if (ReferenceEquals(tipo, TipoLince.Decimal))
            {
                _il.Emit(OpCodes.Ldc_R8, 0.0);
            }
            else if (ReferenceEquals(tipo, TipoLince.Logico))
            {
                _il.Emit(OpCodes.Ldc_I4_0);
            }
            else if (tipo.EsEstructura)
            {
                var temporal = _il.DeclareLocal(_tabla.Clr(tipo));
                _il.Emit(OpCodes.Ldloca, temporal);
                _il.Emit(OpCodes.Initobj, _tabla.Clr(tipo));
                _il.Emit(OpCodes.Ldloc, temporal);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
        }

        // ----- Variables: locales, parámetros y capturadas -----

        private bool EsCapturada(SimboloVariable variable) => _contexto?.Capturadas.ContainsKey(variable) == true;

        /// <summary>Deja en la pila el objeto de cierre: en la función es una local; en la lambda, 'este'.</summary>
        private void EmitirCierre()
        {
            if (_esLambda)
            {
                _il.Emit(OpCodes.Ldarg_0);
            }
            else
            {
                _il.Emit(OpCodes.Ldloc, _cierre!);
            }
        }

        private void EmitirCargaDeVariable(SimboloVariable variable)
        {
            if (EsCapturada(variable))
            {
                EmitirCierre();
                _il.Emit(OpCodes.Ldfld, _contexto!.Capturadas[variable]);
            }
            else if (variable.Clase == ClaseDeVariable.Parametro)
            {
                EmitirCargaDeArgumento(_il, variable.IndiceDeParametro + _desplazamientoDeArgumentos);
            }
            else
            {
                _il.Emit(OpCodes.Ldloc, Local(variable));
            }
        }

        /// <summary>Guarda en la variable el valor que deja <paramref name="emitirValor"/>.</summary>
        private void Guardar(SimboloVariable variable, Action emitirValor)
        {
            if (EsCapturada(variable))
            {
                EmitirCierre();
                emitirValor();
                _il.Emit(OpCodes.Stfld, _contexto!.Capturadas[variable]);
            }
            else
            {
                emitirValor();
                _il.Emit(OpCodes.Stloc, Local(variable));
            }
        }

        public static void EmitirCargaDeArgumento(ILGenerator il, int indice)
        {
            if (indice <= byte.MaxValue)
            {
                il.Emit(OpCodes.Ldarg_S, (byte)indice);
            }
            else
            {
                il.Emit(OpCodes.Ldarg, (short)indice);
            }
        }

        private LocalBuilder Local(SimboloVariable variable)
        {
            if (!_locales.TryGetValue(variable, out var local))
            {
                local = _il.DeclareLocal(_tabla.Clr(variable.Tipo));
                _locales[variable] = local;
            }

            return local;
        }

        // ----- Sentencias -----

        private void EmitirBloque(BloqueEnlazado bloque)
        {
            foreach (var sentencia in bloque.Sentencias)
            {
                EmitirSentencia(sentencia);
            }
        }

        private void EmitirSentencia(SentenciaEnlazada sentencia)
        {
            switch (sentencia)
            {
                case BloqueEnlazado bloque:
                    EmitirBloque(bloque);
                    break;

                case DeclaracionVariableEnlazada declaracion:
                    Guardar(declaracion.Variable, () => EmitirExpresion(declaracion.Valor));
                    break;

                case AsignacionEnlazada asignacion:
                    Guardar(asignacion.Variable, () => EmitirExpresion(asignacion.Valor));
                    break;

                case AsignacionCampoEnlazada asignacion:
                    EmitirReceptor(asignacion.Objeto);
                    EmitirExpresion(asignacion.Valor);
                    _il.Emit(OpCodes.Stfld, _tabla.Campos[asignacion.Campo]);
                    break;

                case AsignacionIndiceEnlazada asignacion:
                    EmitirExpresion(asignacion.Objeto);
                    EmitirIndice(asignacion.Objeto.Tipo, asignacion.Indice);
                    EmitirExpresion(asignacion.Valor);
                    _il.Emit(OpCodes.Callvirt, MetodoDeColeccion(asignacion.Objeto.Tipo, Colecciones.ListaEscribir, Colecciones.MapaEscribir));
                    break;

                case AsignacionIndiceExternaEnlazada asignacion:
                    if (asignacion.Elemento is not null)
                    {
                        EmitirExpresion(asignacion.Objeto);
                        EmitirExpresion(asignacion.Indices[0]);
                        _il.Emit(OpCodes.Conv_I4);
                        EmitirArgumentoAlLimite(asignacion.Valor, asignacion.Elemento);
                        _il.Emit(OpCodes.Stelem, asignacion.Elemento);
                        break;
                    }

                    {
                        EmitirReceptorExterno(asignacion.Objeto, asignacion.Escritor!.DeclaringType!);
                        var parametros = asignacion.Escritor.GetParameters();
                        for (var i = 0; i < asignacion.Indices.Length; i++)
                        {
                            EmitirArgumentoAlLimite(asignacion.Indices[i], parametros[i].ParameterType);
                        }

                        EmitirArgumentoAlLimite(asignacion.Valor, parametros[^1].ParameterType);
                        EmitirLlamadaExterna(asignacion.Escritor, asignacion.Objeto);
                        break;
                    }

                case AsignacionExternaEnlazada asignacion:
                    if (asignacion.Receptor is not null)
                    {
                        EmitirReceptorExterno(asignacion.Receptor, asignacion.Miembro.DeclaringType!);
                    }

                    if (asignacion.Miembro is PropertyInfo propiedad)
                    {
                        EmitirArgumentoAlLimite(asignacion.Valor, propiedad.PropertyType);
                        EmitirLlamadaExterna(propiedad.SetMethod!, asignacion.Receptor);
                    }
                    else
                    {
                        var campo = (FieldInfo)asignacion.Miembro;
                        EmitirArgumentoAlLimite(asignacion.Valor, campo.FieldType);
                        _il.Emit(campo.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, campo);
                    }

                    break;

                case SiEnlazado si:
                    EmitirSi(si);
                    break;

                case MientrasEnlazado mientras:
                    EmitirMientras(mientras);
                    break;

                case ParaEnlazado para:
                    EmitirPara(para);
                    break;

                case ParaEnColeccionEnlazado para:
                    EmitirParaEnColeccion(para);
                    break;

                case RegresarEnlazado regresar:
                    if (regresar.Valor is not null)
                    {
                        EmitirExpresion(regresar.Valor);
                    }

                    _il.Emit(OpCodes.Ret);
                    break;

                case RomperEnlazado:
                    _il.Emit(OpCodes.Br, _ciclos.Peek().Romper);
                    break;

                case ContinuarEnlazado:
                    _il.Emit(OpCodes.Br, _ciclos.Peek().Continuar);
                    break;

                case ExpresionSentenciaEnlazada expresion:
                    EmitirExpresion(expresion.Expresion);
                    if (!ReferenceEquals(expresion.Expresion.Tipo, TipoLince.Nada))
                    {
                        _il.Emit(OpCodes.Pop);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Sentencia no contemplada en la emisión: {sentencia.GetType().Name}");
            }
        }

        private void EmitirSi(SiEnlazado si)
        {
            var sino = _il.DefineLabel();
            var fin = _il.DefineLabel();

            EmitirExpresion(si.Condicion);
            _il.Emit(OpCodes.Brfalse, sino);
            EmitirSentencia(si.Entonces);
            _il.Emit(OpCodes.Br, fin);
            _il.MarkLabel(sino);
            if (si.Sino is not null)
            {
                EmitirSentencia(si.Sino);
            }

            _il.MarkLabel(fin);
        }

        private void EmitirMientras(MientrasEnlazado mientras)
        {
            var inicio = _il.DefineLabel();
            var fin = _il.DefineLabel();

            _il.MarkLabel(inicio);
            EmitirExpresion(mientras.Condicion);
            _il.Emit(OpCodes.Brfalse, fin);

            _ciclos.Push((fin, inicio));
            EmitirBloque(mientras.Cuerpo);
            _ciclos.Pop();

            _il.Emit(OpCodes.Br, inicio);
            _il.MarkLabel(fin);
        }

        private void EmitirPara(ParaEnlazado para)
        {
            var limite = Local(para.Limite);
            var condicion = _il.DefineLabel();
            var continuar = _il.DefineLabel();
            var fin = _il.DefineLabel();

            Guardar(para.Variable, () => EmitirExpresion(para.Desde));
            EmitirExpresion(para.Hasta);
            _il.Emit(OpCodes.Stloc, limite);

            _il.MarkLabel(condicion);
            EmitirCargaDeVariable(para.Variable);
            _il.Emit(OpCodes.Ldloc, limite);
            _il.Emit(OpCodes.Bge, fin);

            _ciclos.Push((fin, continuar));
            EmitirBloque(para.Cuerpo);
            _ciclos.Pop();

            _il.MarkLabel(continuar);
            Guardar(para.Variable, () =>
            {
                EmitirCargaDeVariable(para.Variable);
                _il.Emit(OpCodes.Ldc_I8, 1L);
                _il.Emit(OpCodes.Add);
            });
            _il.Emit(OpCodes.Br, condicion);

            _il.MarkLabel(fin);
        }

        /// <summary>
        /// <c>para x en coleccion</c>: la colección se guarda una vez y se recorre por índice,
        /// como <c>para i en 0..cantidad</c> con <c>x = coleccion[i]</c> al empezar cada vuelta.
        /// </summary>
        private void EmitirParaEnColeccion(ParaEnColeccionEnlazado para)
        {
            var tipoLista = para.Lista.Tipo;
            var coleccion = Local(para.Coleccion);
            var indice = Local(para.Indice);
            var condicion = _il.DefineLabel();
            var continuar = _il.DefineLabel();
            var fin = _il.DefineLabel();

            EmitirExpresion(para.Lista);
            _il.Emit(OpCodes.Stloc, coleccion);
            _il.Emit(OpCodes.Ldc_I8, 0L);
            _il.Emit(OpCodes.Stloc, indice);

            _il.MarkLabel(condicion);
            _il.Emit(OpCodes.Ldloc, indice);
            _il.Emit(OpCodes.Ldloc, coleccion);
            _il.Emit(OpCodes.Callvirt, _tabla.Metodo(_tabla.Clr(tipoLista), Colecciones.ListaCantidad));
            _il.Emit(OpCodes.Conv_I8);
            _il.Emit(OpCodes.Bge, fin);

            Guardar(para.Variable, () =>
            {
                _il.Emit(OpCodes.Ldloc, coleccion);
                _il.Emit(OpCodes.Ldloc, indice);
                _il.Emit(OpCodes.Conv_I4);
                _il.Emit(OpCodes.Callvirt, _tabla.Metodo(_tabla.Clr(tipoLista), Colecciones.ListaLeer));
            });

            _ciclos.Push((fin, continuar));
            EmitirBloque(para.Cuerpo);
            _ciclos.Pop();

            _il.MarkLabel(continuar);
            _il.Emit(OpCodes.Ldloc, indice);
            _il.Emit(OpCodes.Ldc_I8, 1L);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stloc, indice);
            _il.Emit(OpCodes.Br, condicion);

            _il.MarkLabel(fin);
        }

        /// <summary>Deja en la pila el índice como lo pide .NET: <c>int</c> para una lista, la clave tal cual para un mapa.</summary>
        private void EmitirIndice(TipoLince coleccion, ExpresionEnlazada indice)
        {
            EmitirExpresion(indice);
            if (coleccion.EsLista)
            {
                _il.Emit(OpCodes.Conv_I4);
            }
        }

        private MethodInfo MetodoDeColeccion(TipoLince coleccion, MethodInfo deLista, MethodInfo deMapa) =>
            _tabla.Metodo(_tabla.Clr(coleccion), coleccion.EsLista ? deLista : deMapa);

        // ----- Expresiones -----

        public void EmitirExpresion(ExpresionEnlazada expresion)
        {
            switch (expresion)
            {
                case LiteralEnlazado literal:
                    EmitirLiteral(literal);
                    break;

                case VariableEnlazada variable:
                    EmitirCargaDeVariable(variable.Variable);
                    break;

                case EsteEnlazado este:
                    EmitirEste(este.TipoUsuario, direccion: false);
                    break;

                case CampoEnlazado campo:
                    EmitirReceptorDeLectura(campo.Objeto);
                    _il.Emit(OpCodes.Ldfld, _tabla.Campos[campo.Campo]);
                    break;

                case NuevoEnlazado nuevo:
                    foreach (var argumento in nuevo.Argumentos)
                    {
                        EmitirExpresion(argumento);
                    }

                    _il.Emit(OpCodes.Newobj, _tabla.Constructores[nuevo.TipoUsuario]);
                    break;

                case ListaLiteralEnlazado lista:
                    {
                        var clr = _tabla.Clr(lista.TipoLista);
                        _il.Emit(OpCodes.Newobj, _tabla.Constructor(clr, Colecciones.ListaVacia));
                        var agregar = _tabla.Metodo(clr, Colecciones.ListaAgregar);
                        foreach (var elemento in lista.Elementos)
                        {
                            _il.Emit(OpCodes.Dup);
                            EmitirExpresion(elemento);
                            _il.Emit(OpCodes.Callvirt, agregar);
                        }

                        break;
                    }

                case MapaLiteralEnlazado mapa:
                    {
                        var clr = _tabla.Clr(mapa.TipoMapa);
                        _il.Emit(OpCodes.Newobj, _tabla.Constructor(clr, Colecciones.MapaVacio));
                        var escribir = _tabla.Metodo(clr, Colecciones.MapaEscribir);
                        foreach (var par in mapa.Pares)
                        {
                            _il.Emit(OpCodes.Dup);
                            EmitirExpresion(par.Clave);
                            EmitirExpresion(par.Valor);
                            _il.Emit(OpCodes.Callvirt, escribir);
                        }

                        break;
                    }

                case NuevaColeccionEnlazada nueva:
                    _il.Emit(OpCodes.Newobj, _tabla.Constructor(_tabla.Clr(nueva.TipoColeccion),
                        nueva.TipoColeccion.EsLista ? Colecciones.ListaVacia : Colecciones.MapaVacio));
                    break;

                case IndiceEnlazado indice:
                    EmitirExpresion(indice.Objeto);
                    EmitirIndice(indice.Objeto.Tipo, indice.Indice);
                    _il.Emit(OpCodes.Callvirt, MetodoDeColeccion(indice.Objeto.Tipo, Colecciones.ListaLeer, Colecciones.MapaLeer));
                    break;

                case MiembroIntegradoEnlazado integrado:
                    EmitirMiembroIntegrado(integrado);
                    break;

                case TipoComoReceptorEnlazado receptor:
                    throw new InvalidOperationException($"'{receptor.TipoExterno.Nombre}' es un tipo y no se puede emitir como valor; el enlazador debió informarlo.");

                case IndiceExternoEnlazado indice:
                    if (indice.Elemento is not null)
                    {
                        EmitirExpresion(indice.Objeto);
                        EmitirExpresion(indice.Indices[0]);
                        _il.Emit(OpCodes.Conv_I4);
                        _il.Emit(OpCodes.Ldelem, indice.Elemento);
                        AjustarResultadoDelLimite(indice.Elemento, indice.Tipo);
                        break;
                    }

                    {
                        EmitirReceptorExterno(indice.Objeto, indice.Lector!.DeclaringType!);
                        var parametros = indice.Lector.GetParameters();
                        for (var i = 0; i < indice.Indices.Length; i++)
                        {
                            EmitirArgumentoAlLimite(indice.Indices[i], parametros[i].ParameterType);
                        }

                        EmitirLlamadaExterna(indice.Lector, indice.Objeto);
                        AjustarResultadoDelLimite(indice.Lector.ReturnType, indice.Tipo);
                        break;
                    }

                case LlamadaExternaEnlazada llamada:
                    {
                        if (llamada.Receptor is not null)
                        {
                            EmitirReceptorExterno(llamada.Receptor, llamada.Metodo.DeclaringType!);
                        }

                        var parametros = llamada.Metodo.GetParameters();
                        for (var i = 0; i < llamada.Argumentos.Length; i++)
                        {
                            EmitirArgumentoAlLimite(llamada.Argumentos[i], parametros[i].ParameterType);
                        }

                        EmitirPredeterminados(parametros, llamada.Argumentos.Length);
                        EmitirLlamadaExterna(llamada.Metodo, llamada.Receptor);
                        AjustarResultadoDelLimite(llamada.Metodo.ReturnType, llamada.Tipo);
                        break;
                    }

                case NuevoExternoEnlazado nuevo:
                    if (nuevo.Constructor is null)
                    {
                        // Una estructura de .NET sin constructor: el valor a cero.
                        var temporal = _il.DeclareLocal(nuevo.TipoExterno.TipoClr);
                        _il.Emit(OpCodes.Ldloca, temporal);
                        _il.Emit(OpCodes.Initobj, nuevo.TipoExterno.TipoClr);
                        _il.Emit(OpCodes.Ldloc, temporal);
                        break;
                    }

                    {
                        var parametros = nuevo.Constructor.GetParameters();
                        for (var i = 0; i < nuevo.Argumentos.Length; i++)
                        {
                            EmitirArgumentoAlLimite(nuevo.Argumentos[i], parametros[i].ParameterType);
                        }

                        EmitirPredeterminados(parametros, nuevo.Argumentos.Length);
                        _il.Emit(OpCodes.Newobj, nuevo.Constructor);
                        break;
                    }

                case MiembroExternoEnlazado miembro:
                    if (miembro.Receptor is not null)
                    {
                        EmitirReceptorExterno(miembro.Receptor, miembro.Miembro.DeclaringType!);
                    }

                    if (miembro.Miembro is PropertyInfo propiedad)
                    {
                        EmitirLlamadaExterna(propiedad.GetMethod!, miembro.Receptor);
                        AjustarResultadoDelLimite(propiedad.PropertyType, miembro.Tipo);
                    }
                    else
                    {
                        var campo = (FieldInfo)miembro.Miembro;
                        if (campo.IsLiteral)
                        {
                            // Una constante (los miembros de una enumeración, Math.PI): no se lee de un campo, se emite el valor.
                            EmitirConstante(campo.GetRawConstantValue());
                        }
                        else
                        {
                            _il.Emit(campo.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, campo);
                        }

                        AjustarResultadoDelLimite(campo.FieldType, miembro.Tipo);
                    }

                    break;

                case LambdaEnlazada lambda:
                    // El delegado: sobre el cierre si la función tiene uno, o sobre nada (método estático).
                    if (TieneCierre)
                    {
                        EmitirCierre();
                    }
                    else
                    {
                        _il.Emit(OpCodes.Ldnull);
                    }

                    _il.Emit(OpCodes.Ldftn, _contexto!.Metodos[lambda.Simbolo]);
                    _il.Emit(OpCodes.Newobj, lambda.DelegadoClr is { } delegado
                        ? delegado.GetConstructor([typeof(object), typeof(IntPtr)])!
                        : _tabla.ConstructorDeDelegado(lambda.TipoFuncion));
                    break;

                case ReferenciaFuncionEnlazada referencia:
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Ldftn, _tabla.Metodos[referencia.Funcion]);
                    _il.Emit(OpCodes.Newobj, _tabla.ConstructorDeDelegado(referencia.TipoFuncion));
                    break;

                case InvocacionEnlazada invocacion:
                    EmitirExpresion(invocacion.Funcion);
                    foreach (var argumento in invocacion.Argumentos)
                    {
                        EmitirExpresion(argumento);
                    }

                    _il.Emit(OpCodes.Callvirt, _tabla.InvocarDelegado((TipoFuncion)invocacion.Funcion.Tipo.SinNulo));
                    break;

                case LlamadaMetodoEnlazada llamada:
                    EmitirReceptor(llamada.Objeto);
                    foreach (var argumento in llamada.Argumentos)
                    {
                        EmitirExpresion(argumento);
                    }

                    _il.Emit(llamada.Objeto.Tipo.EsEstructura ? OpCodes.Call : OpCodes.Callvirt, _tabla.Metodos[llamada.Metodo]);
                    break;

                case UnariaEnlazada unaria:
                    EmitirExpresion(unaria.Operando);
                    if (unaria.Operador == OperadorUnario.Negativo)
                    {
                        _il.Emit(OpCodes.Neg);
                    }
                    else
                    {
                        _il.Emit(OpCodes.Ldc_I4_0);
                        _il.Emit(OpCodes.Ceq);
                    }

                    break;

                case BinariaEnlazada binaria:
                    EmitirBinaria(binaria);
                    break;

                case ConversionEnlazada conversion:
                    EmitirConversion(conversion);
                    break;

                case LlamadaEnlazada llamada:
                    foreach (var argumento in llamada.Argumentos)
                    {
                        EmitirExpresion(argumento);
                    }

                    _il.Emit(OpCodes.Call, _tabla.Metodos[llamada.Funcion]);
                    break;

                case ImprimirEnlazado imprimir:
                    if (imprimir.Valor is null)
                    {
                        _il.Emit(OpCodes.Call, Clr.EscribirLineaVacia);
                    }
                    else
                    {
                        EmitirExpresion(imprimir.Valor);
                        _il.Emit(OpCodes.Call, Clr.EscribirLinea);
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Expresión no contemplada en la emisión: {expresion.GetType().Name}");
            }
        }

        /// <summary>
        /// <c>este</c>: en un método es el argumento 0 (una referencia, o un puntero si es una
        /// estructura); en una lambda está guardado en el cierre.
        /// </summary>
        private void EmitirEste(TipoUsuario tipo, bool direccion)
        {
            if (_esLambda)
            {
                EmitirCierre();
                _il.Emit(direccion ? OpCodes.Ldflda : OpCodes.Ldfld, _contexto!.CampoEste!);
                return;
            }

            _il.Emit(OpCodes.Ldarg_0);
            if (tipo.EsEstructura && !direccion)
            {
                // En una estructura 'este' es un puntero: para usarlo como valor hay que leerlo.
                _il.Emit(OpCodes.Ldobj, _tabla.Clr(tipo));
            }
        }

        // ----- La frontera con .NET -----

        /// <summary>
        /// El receptor de un miembro de .NET: si el miembro está declarado en un tipo por valor
        /// (DateTime, pero también Int64 cuando se llama 255.ToString("X")), hace falta su
        /// dirección; si no, la referencia.
        /// </summary>
        private void EmitirReceptorExterno(ExpresionEnlazada receptor, Type declarante)
        {
            if (declarante.IsValueType)
            {
                EmitirDireccion(receptor);
            }
            else
            {
                EmitirExpresion(receptor);
            }
        }

        /// <summary><c>call</c> para estáticos y estructuras, <c>callvirt</c> para instancias por referencia.</summary>
        private void EmitirLlamadaExterna(MethodInfo metodo, ExpresionEnlazada? receptor)
        {
            var virtual_ = receptor is not null && !metodo.IsStatic && !metodo.DeclaringType!.IsValueType;
            _il.Emit(virtual_ ? OpCodes.Callvirt : OpCodes.Call, metodo);
        }

        /// <summary>
        /// Deja en la pila un valor de Lince como lo pide un parámetro de .NET: <c>entero</c> se
        /// estrecha a <c>int</c> y compañía, <c>decimal</c> a <c>float</c>, y un valor se
        /// empaqueta si el parámetro es <c>object</c> u otra referencia.
        /// </summary>
        private void EmitirArgumentoAlLimite(ExpresionEnlazada argumento, Type parametro)
        {
            EmitirExpresion(argumento);
            var origen = argumento.Tipo.SinNulo;
            if (ReferenceEquals(argumento.Tipo, TipoLince.Nulo))
            {
                return;
            }

            if (ReferenceEquals(origen, TipoLince.Entero))
            {
                if (parametro == typeof(int)) _il.Emit(OpCodes.Conv_I4);
                else if (parametro == typeof(short)) _il.Emit(OpCodes.Conv_I2);
                else if (parametro == typeof(byte)) _il.Emit(OpCodes.Conv_U1);
                else if (parametro == typeof(sbyte)) _il.Emit(OpCodes.Conv_I1);
                else if (parametro == typeof(ushort)) _il.Emit(OpCodes.Conv_U2);
                else if (parametro == typeof(uint)) _il.Emit(OpCodes.Conv_U4);
                else if (parametro == typeof(double)) _il.Emit(OpCodes.Conv_R8);
                else if (parametro == typeof(float)) _il.Emit(OpCodes.Conv_R4);
                else if (!parametro.IsValueType) _il.Emit(OpCodes.Box, typeof(long));
                return;
            }

            if (ReferenceEquals(origen, TipoLince.Decimal))
            {
                if (parametro == typeof(float)) _il.Emit(OpCodes.Conv_R4);
                else if (!parametro.IsValueType) _il.Emit(OpCodes.Box, typeof(double));
                return;
            }

            if (ReferenceEquals(origen, TipoLince.Logico))
            {
                if (!parametro.IsValueType) _il.Emit(OpCodes.Box, typeof(bool));
                return;
            }

            if (ReferenceEquals(origen, TipoLince.Texto) && parametro == typeof(char))
            {
                // Un texto de un carácter como char: el literal se emite como constante; otro texto, su primer carácter.
                if (argumento is LiteralEnlazado { Valor: string { Length: 1 } literal })
                {
                    _il.Emit(OpCodes.Pop);
                    _il.Emit(OpCodes.Ldc_I4, (int)literal[0]);
                }
                else
                {
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Callvirt, Clr.CaracterEn);
                }

                return;
            }

            if (origen.EsEstructura && !parametro.IsValueType)
            {
                _il.Emit(OpCodes.Box, _tabla.Clr(origen));
            }
        }

        /// <summary>Lo que devuelve .NET pasa al tipo de Lince: los enteros cortos a <c>entero</c> (64 bits), <c>float</c> a <c>decimal</c>.</summary>
        private void AjustarResultadoDelLimite(Type resultado, TipoLince tipo)
        {
            if (ReferenceEquals(tipo, TipoLince.Entero) && resultado != typeof(long))
            {
                _il.Emit(resultado == typeof(uint) ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
            }
            else if (ReferenceEquals(tipo, TipoLince.Decimal) && resultado == typeof(float))
            {
                _il.Emit(OpCodes.Conv_R8);
            }
            else if (ReferenceEquals(tipo, TipoLince.Texto) && resultado == typeof(char))
            {
                _il.Emit(OpCodes.Call, Clr.CaracterATexto);
            }
        }

        /// <summary>Los parámetros opcionales que la llamada no dio: su valor por defecto, como haría C#.</summary>
        private void EmitirPredeterminados(ParameterInfo[] parametros, int desde)
        {
            for (var i = desde; i < parametros.Length; i++)
            {
                var parametro = parametros[i];
                var valor = parametro.DefaultValue;
                if (valor is Enum enumeracion)
                {
                    valor = Convert.ChangeType(enumeracion, Enum.GetUnderlyingType(enumeracion.GetType()), CultureInfo.InvariantCulture);
                }

                if (valor is null && parametro.ParameterType.IsValueType)
                {
                    var temporal = _il.DeclareLocal(parametro.ParameterType);
                    _il.Emit(OpCodes.Ldloca, temporal);
                    _il.Emit(OpCodes.Initobj, parametro.ParameterType);
                    _il.Emit(OpCodes.Ldloc, temporal);
                    continue;
                }

                EmitirConstante(valor);
            }
        }

        private void EmitirConstante(object? valor)
        {
            switch (valor)
            {
                case int i: _il.Emit(OpCodes.Ldc_I4, i); break;
                case long l: _il.Emit(OpCodes.Ldc_I8, l); break;
                case short s: _il.Emit(OpCodes.Ldc_I4, (int)s); break;
                case byte b: _il.Emit(OpCodes.Ldc_I4, (int)b); break;
                case sbyte sb: _il.Emit(OpCodes.Ldc_I4, (int)sb); break;
                case ushort us: _il.Emit(OpCodes.Ldc_I4, (int)us); break;
                case uint ui: _il.Emit(OpCodes.Ldc_I4, unchecked((int)ui)); break;
                case ulong ul: _il.Emit(OpCodes.Ldc_I8, unchecked((long)ul)); break;
                case double d: _il.Emit(OpCodes.Ldc_R8, d); break;
                case float f: _il.Emit(OpCodes.Ldc_R4, f); break;
                case bool b: _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); break;
                case char c: _il.Emit(OpCodes.Ldc_I4, (int)c); break;
                case string s: _il.Emit(OpCodes.Ldstr, s); break;
                case null: _il.Emit(OpCodes.Ldnull); break;
                default: throw new InvalidOperationException($"Constante de .NET no contemplada: {valor.GetType().Name}");
            }
        }

        /// <summary>Un miembro integrado: el receptor, los argumentos ajustados, el extra que pida .NET, la llamada y el posproceso.</summary>
        private void EmitirMiembroIntegrado(MiembroIntegradoEnlazado integrado)
        {
            var receta = integrado.Miembro.Receta;
            if (!receta.ReceptorAlFinal)
            {
                EmitirExpresion(integrado.Objeto);
            }

            if (receta.Metodo is not null)
            {
                for (var i = 0; i < integrado.Argumentos.Length; i++)
                {
                    EmitirExpresion(integrado.Argumentos[i]);
                    if (receta.ArgumentoAInt32(i))
                    {
                        _il.Emit(OpCodes.Conv_I4);
                    }
                }

                if (receta.ReceptorAlFinal)
                {
                    EmitirExpresion(integrado.Objeto);
                }

                switch (receta.Extra)
                {
                    case ArgumentoExtra.CulturaInvariante:
                        _il.Emit(OpCodes.Call, Clr.CulturaInvariante);
                        break;
                    case ArgumentoExtra.ComparacionOrdinal:
                        _il.Emit(OpCodes.Ldc_I4, (int)StringComparison.Ordinal);
                        break;
                    case ArgumentoExtra.OpcionesDeSplitNinguna:
                        _il.Emit(OpCodes.Ldc_I4, (int)StringSplitOptions.None);
                        break;
                }

                var metodo = receta.Metodo.IsGenericMethodDefinition
                    ? receta.Metodo.MakeGenericMethod(receta.ArgumentosGenericos.Select(_tabla.Clr).ToArray())
                    : _tabla.Metodo(_tabla.Clr(integrado.Objeto.Tipo), receta.Metodo);
                _il.Emit(receta.EsEstatico ? OpCodes.Call : OpCodes.Callvirt, metodo);
            }

            switch (receta.Posproceso)
            {
                case Posproceso.AEntero64:
                case Posproceso.AEnteroTruncando:
                    _il.Emit(OpCodes.Conv_I8);
                    break;
                case Posproceso.ADecimal:
                    _il.Emit(OpCodes.Conv_R8);
                    break;
                case Posproceso.ALista:
                    // Lo que devolvió .NET (un arreglo, las claves, una secuencia de LINQ) se copia a una lista nueva.
                    _il.Emit(OpCodes.Newobj, _tabla.Constructor(_tabla.Clr(integrado.Tipo), Colecciones.ListaDesde));
                    break;
            }
        }

        /// <summary>
        /// Deja en la pila el receptor de una llamada a método o de una escritura de campo: la
        /// referencia si es una clase, la dirección si es una estructura (para que el método o
        /// la escritura actúen sobre el original y no sobre una copia).
        /// </summary>
        private void EmitirReceptor(ExpresionEnlazada objeto)
        {
            if (objeto.Tipo.EsEstructura)
            {
                EmitirDireccion(objeto);
            }
            else
            {
                EmitirExpresion(objeto);
            }
        }

        /// <summary>Para leer un campo basta con el valor; en una estructura 'este' ya es un puntero y sirve tal cual.</summary>
        private void EmitirReceptorDeLectura(ExpresionEnlazada objeto)
        {
            if (objeto is EsteEnlazado este)
            {
                EmitirEste(este.TipoUsuario, direccion: este.TipoUsuario.EsEstructura);
            }
            else
            {
                EmitirExpresion(objeto);
            }
        }

        /// <summary>Deja en la pila la dirección de una estructura.</summary>
        private void EmitirDireccion(ExpresionEnlazada objeto)
        {
            switch (objeto)
            {
                case EsteEnlazado este:
                    EmitirEste(este.TipoUsuario, direccion: true);
                    break;

                case VariableEnlazada variable when EsCapturada(variable.Variable):
                    EmitirCierre();
                    _il.Emit(OpCodes.Ldflda, _contexto!.Capturadas[variable.Variable]);
                    break;

                case VariableEnlazada { Variable.Clase: ClaseDeVariable.Parametro } variable:
                    _il.Emit(OpCodes.Ldarga, (short)(variable.Variable.IndiceDeParametro + _desplazamientoDeArgumentos));
                    break;

                case VariableEnlazada variable:
                    _il.Emit(OpCodes.Ldloca, Local(variable.Variable));
                    break;

                case CampoEnlazado campo:
                    EmitirReceptor(campo.Objeto);
                    _il.Emit(OpCodes.Ldflda, _tabla.Campos[campo.Campo]);
                    break;

                default:
                    // Un valor temporal (el resultado de una llamada o de 'nuevo'): se copia a una local.
                    var temporal = _il.DeclareLocal(_tabla.Clr(objeto.Tipo));
                    EmitirExpresion(objeto);
                    _il.Emit(OpCodes.Stloc, temporal);
                    _il.Emit(OpCodes.Ldloca, temporal);
                    break;
            }
        }

        private void EmitirLiteral(LiteralEnlazado literal)
        {
            switch (literal.Valor)
            {
                case long entero:
                    _il.Emit(OpCodes.Ldc_I8, entero);
                    break;
                case double decimalValor:
                    _il.Emit(OpCodes.Ldc_R8, decimalValor);
                    break;
                case string texto:
                    _il.Emit(OpCodes.Ldstr, texto);
                    break;
                case bool logico:
                    _il.Emit(logico ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    break;
                case null:
                    _il.Emit(OpCodes.Ldnull);
                    break;
                default:
                    throw new InvalidOperationException($"Literal no contemplado: {literal.Valor.GetType().Name}");
            }
        }

        private void EmitirBinaria(BinariaEnlazada binaria)
        {
            switch (binaria.Operador)
            {
                case OperadorBinario.YLogico:
                    EmitirCortocircuito(binaria, saltarSi: false);
                    return;
                case OperadorBinario.OLogico:
                    EmitirCortocircuito(binaria, saltarSi: true);
                    return;
            }

            EmitirExpresion(binaria.Izquierda);
            EmitirExpresion(binaria.Derecha);

            var esTexto = binaria.Izquierda.Tipo.EsTextual;
            var esDecimal = ReferenceEquals(binaria.Izquierda.Tipo, TipoLince.Decimal);

            switch (binaria.Operador)
            {
                case OperadorBinario.Concatenacion:
                    _il.Emit(OpCodes.Call, Clr.Concatenar);
                    break;
                case OperadorBinario.Suma:
                    _il.Emit(OpCodes.Add);
                    break;
                case OperadorBinario.Resta:
                    _il.Emit(OpCodes.Sub);
                    break;
                case OperadorBinario.Multiplicacion:
                    _il.Emit(OpCodes.Mul);
                    break;
                case OperadorBinario.Division:
                    _il.Emit(OpCodes.Div);
                    break;
                case OperadorBinario.Modulo:
                    _il.Emit(OpCodes.Rem);
                    break;
                case OperadorBinario.Igual:
                    if (esTexto)
                    {
                        _il.Emit(OpCodes.Call, Clr.TextosIguales);
                    }
                    else
                    {
                        _il.Emit(OpCodes.Ceq);
                    }

                    break;
                case OperadorBinario.Distinto:
                    if (esTexto)
                    {
                        _il.Emit(OpCodes.Call, Clr.TextosIguales);
                    }
                    else
                    {
                        _il.Emit(OpCodes.Ceq);
                    }

                    EmitirNegacionLogica();
                    break;
                case OperadorBinario.Menor:
                    _il.Emit(OpCodes.Clt);
                    break;
                case OperadorBinario.Mayor:
                    _il.Emit(OpCodes.Cgt);
                    break;
                case OperadorBinario.MenorIgual:
                    // a <= b  es  !(a > b); con decimales se usa la forma "unordered" para que NaN dé falso.
                    _il.Emit(esDecimal ? OpCodes.Cgt_Un : OpCodes.Cgt);
                    EmitirNegacionLogica();
                    break;
                case OperadorBinario.MayorIgual:
                    _il.Emit(esDecimal ? OpCodes.Clt_Un : OpCodes.Clt);
                    EmitirNegacionLogica();
                    break;
                default:
                    throw new InvalidOperationException($"Operador no contemplado: {binaria.Operador}");
            }
        }

        private void EmitirNegacionLogica()
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Ceq);
        }

        /// <summary>
        /// <c>a &amp;&amp; b</c>: si a es falso, el resultado es falso sin evaluar b.
        /// <c>a || b</c>: si a es verdadero, el resultado es verdadero sin evaluar b.
        /// </summary>
        private void EmitirCortocircuito(BinariaEnlazada binaria, bool saltarSi)
        {
            var atajo = _il.DefineLabel();
            var fin = _il.DefineLabel();

            EmitirExpresion(binaria.Izquierda);
            _il.Emit(saltarSi ? OpCodes.Brtrue : OpCodes.Brfalse, atajo);
            EmitirExpresion(binaria.Derecha);
            _il.Emit(OpCodes.Br, fin);
            _il.MarkLabel(atajo);
            _il.Emit(saltarSi ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.MarkLabel(fin);
        }

        private void EmitirConversion(ConversionEnlazada conversion)
        {
            var origen = conversion.Expresion.Tipo;
            var destino = conversion.Tipo;
            EmitirExpresion(conversion.Expresion);

            if (ReferenceEquals(destino, TipoLince.Decimal) && ReferenceEquals(origen, TipoLince.Entero))
            {
                _il.Emit(OpCodes.Conv_R8);
                return;
            }

            if (ReferenceEquals(origen, TipoLince.Nulo) && destino.EsReferencia)
            {
                // nulo → texto? o clase?: la referencia nula ya está en la pila.
                return;
            }

            if (ReferenceEquals(origen.SinNulo, destino.SinNulo))
            {
                // T → T? y T? → T (donde el flujo lo permitió): en .NET son el mismo tipo.
                return;
            }

            if (destino.SinNulo is TipoExterno externo)
            {
                // Hacia object o una base de .NET: los valores se empaquetan, las referencias van tal cual.
                if (!externo.EsValor && (origen.SinNulo.EsEstructura || ReferenceEquals(origen, TipoLince.Entero)
                    || ReferenceEquals(origen, TipoLince.Decimal) || ReferenceEquals(origen, TipoLince.Logico)))
                {
                    _il.Emit(OpCodes.Box, _tabla.Clr(origen));
                }

                return;
            }

            if (destino.EsTextual && origen.SinNulo is TipoExterno origenExterno)
            {
                // Un valor de .NET se escribe con su ToString; string.Concat(object) tolera nulo.
                if (origenExterno.EsValor)
                {
                    _il.Emit(OpCodes.Box, origenExterno.TipoClr);
                }

                _il.Emit(OpCodes.Call, Clr.ConcatenarObjeto);
                return;
            }

            if (destino.EsTextual)
            {
                if (ReferenceEquals(origen, TipoLince.Entero))
                {
                    EmitirATexto(_temporalEntero ??= _il.DeclareLocal(typeof(long)), Clr.EnteroATexto);
                }
                else if (ReferenceEquals(origen, TipoLince.Decimal))
                {
                    EmitirATexto(_temporalDecimal ??= _il.DeclareLocal(typeof(double)), Clr.DecimalATexto);
                }
                else if (ReferenceEquals(origen, TipoLince.Logico))
                {
                    var verdadero = _il.DefineLabel();
                    var fin = _il.DefineLabel();
                    _il.Emit(OpCodes.Brtrue, verdadero);
                    _il.Emit(OpCodes.Ldstr, "falso");
                    _il.Emit(OpCodes.Br, fin);
                    _il.MarkLabel(verdadero);
                    _il.Emit(OpCodes.Ldstr, "verdadero");
                    _il.MarkLabel(fin);
                }

                return;
            }

            throw new InvalidOperationException($"Conversión no contemplada: {origen} a {destino}");
        }

        /// <summary>Llama a ToString(cultura invariante) sobre un valor que está en la pila.</summary>
        private void EmitirATexto(LocalBuilder temporal, MethodInfo aTexto)
        {
            _il.Emit(OpCodes.Stloc, temporal);
            _il.Emit(OpCodes.Ldloca, temporal);
            _il.Emit(OpCodes.Call, Clr.CulturaInvariante);
            _il.Emit(OpCodes.Call, aTexto);
        }
    }
}
