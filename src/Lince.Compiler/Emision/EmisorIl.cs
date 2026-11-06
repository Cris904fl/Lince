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
/// <see cref="PersistedAssemblyBuilder"/>. Todas las funciones del archivo son métodos
/// estáticos de una clase <c>Programa</c>; el punto de entrada es un <c>Main</c> generado que
/// llama a <c>principal</c> y convierte su resultado en código de salida.
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
        var clase = modulo.DefineType(NombreDeClase, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);

        // Primero las firmas, para que cualquier cuerpo pueda llamar a cualquier función.
        var metodos = new Dictionary<SimboloFuncion, MethodBuilder>();
        foreach (var funcion in programa.Funciones)
        {
            var simbolo = funcion.Simbolo;
            var metodo = clase.DefineMethod(
                simbolo.Nombre,
                MethodAttributes.Public | MethodAttributes.Static,
                simbolo.TipoRetorno.TipoClr,
                simbolo.Parametros.Select(p => p.Tipo.TipoClr).ToArray());

            for (var i = 0; i < simbolo.Parametros.Length; i++)
            {
                metodo.DefineParameter(i + 1, ParameterAttributes.None, simbolo.Parametros[i].Nombre);
            }

            metodos[simbolo] = metodo;
        }

        foreach (var funcion in programa.Funciones)
        {
            new EmisorDeFuncion(metodos[funcion.Simbolo].GetILGenerator(), funcion, metodos).Emitir();
        }

        var entrada = EmitirEntrada(clase, metodos[programa.Principal], programa.Principal);
        clase.CreateType();

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

    /// <summary>Miembros de .NET que el código generado llama. Se resuelven una vez.</summary>
    private static class Clr
    {
        public static readonly MethodInfo EscribirLinea = Metodo(typeof(Console), nameof(Console.WriteLine), typeof(string));
        public static readonly MethodInfo EscribirLineaVacia = Metodo(typeof(Console), nameof(Console.WriteLine));
        public static readonly MethodInfo Concatenar = Metodo(typeof(string), nameof(string.Concat), typeof(string), typeof(string));
        public static readonly MethodInfo TextosIguales = Metodo(typeof(string), nameof(string.Equals), typeof(string), typeof(string));
        public static readonly MethodInfo CulturaInvariante = typeof(CultureInfo).GetProperty(nameof(CultureInfo.InvariantCulture))!.GetMethod!;
        public static readonly MethodInfo EnteroATexto = Metodo(typeof(long), nameof(long.ToString), typeof(IFormatProvider));
        public static readonly MethodInfo DecimalATexto = Metodo(typeof(double), nameof(double.ToString), typeof(IFormatProvider));
        public static readonly MethodInfo ObtenerUtf8 = typeof(Encoding).GetProperty(nameof(Encoding.UTF8))!.GetMethod!;
        public static readonly MethodInfo FijarCodificacionDeSalida = typeof(Console).GetProperty(nameof(Console.OutputEncoding))!.SetMethod!;

        private static MethodInfo Metodo(Type tipo, string nombre, params Type[] parametros) =>
            tipo.GetMethod(nombre, parametros) ?? throw new MissingMethodException(tipo.FullName, nombre);
    }

    /// <summary>Emite el cuerpo de una función. Una instancia por función: guarda sus locales y sus ciclos.</summary>
    private sealed class EmisorDeFuncion
    {
        private readonly ILGenerator _il;
        private readonly FuncionEnlazada _funcion;
        private readonly Dictionary<SimboloFuncion, MethodBuilder> _metodos;
        private readonly Dictionary<SimboloVariable, LocalBuilder> _locales = new();
        private readonly Stack<(Label Romper, Label Continuar)> _ciclos = new();
        private LocalBuilder? _temporalEntero;
        private LocalBuilder? _temporalDecimal;

        public EmisorDeFuncion(ILGenerator il, FuncionEnlazada funcion, Dictionary<SimboloFuncion, MethodBuilder> metodos)
        {
            _il = il;
            _funcion = funcion;
            _metodos = metodos;
        }

        public void Emitir()
        {
            EmitirBloque(_funcion.Cuerpo);

            // El enlazador ya garantizó que las funciones con valor regresan por todos los
            // caminos; este cierre es inalcanzable pero mantiene el IL bien formado.
            var tipoRetorno = _funcion.Simbolo.TipoRetorno;
            if (!ReferenceEquals(tipoRetorno, TipoLince.Nada))
            {
                EmitirValorPorDefecto(tipoRetorno);
            }

            _il.Emit(OpCodes.Ret);
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
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
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
                    EmitirExpresion(declaracion.Valor);
                    _il.Emit(OpCodes.Stloc, Local(declaracion.Variable));
                    break;

                case AsignacionEnlazada asignacion:
                    EmitirExpresion(asignacion.Valor);
                    _il.Emit(OpCodes.Stloc, Local(asignacion.Variable));
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
            var variable = Local(para.Variable);
            var limite = Local(para.Limite);
            var condicion = _il.DefineLabel();
            var continuar = _il.DefineLabel();
            var fin = _il.DefineLabel();

            EmitirExpresion(para.Desde);
            _il.Emit(OpCodes.Stloc, variable);
            EmitirExpresion(para.Hasta);
            _il.Emit(OpCodes.Stloc, limite);

            _il.MarkLabel(condicion);
            _il.Emit(OpCodes.Ldloc, variable);
            _il.Emit(OpCodes.Ldloc, limite);
            _il.Emit(OpCodes.Bge, fin);

            _ciclos.Push((fin, continuar));
            EmitirBloque(para.Cuerpo);
            _ciclos.Pop();

            _il.MarkLabel(continuar);
            _il.Emit(OpCodes.Ldloc, variable);
            _il.Emit(OpCodes.Ldc_I8, 1L);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Stloc, variable);
            _il.Emit(OpCodes.Br, condicion);

            _il.MarkLabel(fin);
        }

        private LocalBuilder Local(SimboloVariable variable)
        {
            if (!_locales.TryGetValue(variable, out var local))
            {
                local = _il.DeclareLocal(variable.Tipo.TipoClr);
                _locales[variable] = local;
            }

            return local;
        }

        // ----- Expresiones -----

        private void EmitirExpresion(ExpresionEnlazada expresion)
        {
            switch (expresion)
            {
                case LiteralEnlazado literal:
                    EmitirLiteral(literal);
                    break;

                case VariableEnlazada variable:
                    EmitirCargaDeVariable(variable.Variable);
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

                    _il.Emit(OpCodes.Call, _metodos[llamada.Funcion]);
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

        private void EmitirCargaDeVariable(SimboloVariable variable)
        {
            if (variable.Clase == ClaseDeVariable.Parametro)
            {
                if (variable.IndiceDeParametro <= byte.MaxValue)
                {
                    _il.Emit(OpCodes.Ldarg_S, (byte)variable.IndiceDeParametro);
                }
                else
                {
                    _il.Emit(OpCodes.Ldarg, (short)variable.IndiceDeParametro);
                }
            }
            else
            {
                _il.Emit(OpCodes.Ldloc, Local(variable));
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

            var esTexto = ReferenceEquals(binaria.Izquierda.Tipo, TipoLince.Texto);
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

            if (ReferenceEquals(destino, TipoLince.Texto))
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

                // texto → texto y nulo → texto no necesitan instrucciones.
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
