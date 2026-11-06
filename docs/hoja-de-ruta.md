# Hoja de ruta de Lince

Lince se construye por fases. Cada fase deja el compilador usable y con la suite en verde;
nada de una fase se tira en la siguiente.

**La meta** (decidida el 2026-11-06): que con Lince se puedan hacer aplicaciones web, análisis
de datos y programas de escritorio con la comodidad de Python. El camino no es escribir esas
librerías, sino montarse en las de .NET (ASP.NET Core, `Microsoft.Data.Analysis`, MathNet,
ML.NET, ScottPlot, Avalonia) a través de la interoperabilidad, igual que Python se apoya en C.
Por eso la interoperabilidad va antes que las herramientas: es la fase que multiplica.

## Fase 1: el núcleo (hecha, 2026-11-06)

Funciones, `entero`, `decimal`, `texto`, `logico`, aritmética, comparaciones, lógicos con
cortocircuito, `si`/`sino`, `mientras`, `para` sobre rangos, `romper`/`continuar`, variables
locales con `sea`/`var` e inferencia, recursión, `imprimir`, concatenación de textos.
Compila a un ensamblado .NET real con `PersistedAssemblyBuilder`. Diagnósticos con posición y
recuperación de errores. CLI `lince compilar` y `lince ejecutar`. Suite de pruebas por fase.

## Fase 2: objetos (hecha, 2026-11-06)

Sin esto no se puede ni crear una app web ni un DataFrame.

- **Clases** (`clase Cuenta { ... }`): tipos por referencia con campos, métodos, `nuevo`,
  `este`, `privado` (todo es público si no se dice otra cosa) y constructor explícito
  (`funcion nuevo(...)`) o implícito por los campos sin valor inicial. Se emiten como clases
  de .NET.
- **Estructuras** (`estructura Punto { ... }`): lo mismo, por valor. Se emiten como `struct`.
- **Tipos nulables** (`texto?`, `Cuenta?`) con análisis de flujo: `nulo` solo cabe en un tipo
  con `?`, y un `T?` se usa como `T` tras `si x != nulo`, `si x == nulo { regresar; }`,
  `x != nulo && ...` o `mientras x != nulo`. Queda para después: `?` sobre tipos por valor
  (`entero?`, sobre `Nullable<T>`).
- Herencia e interfaces se evalúan en la fase 6, cuando la interoperabilidad las exija.

## Fase 3: colecciones y funciones como valores

Lo que hace cómodo a Python: `datos.filtrar(f => f.edad > 30)`.

- `lista<T>` sobre `List<T>` y `mapa<K, V>` sobre `Dictionary<K, V>`: literales
  (`[1, 2, 3]`, `{"a": 1}`), indexación, `agregar`, `quitar`, `contiene`, `cantidad`.
- `para elemento en coleccion { }` además del rango de enteros.
- Lambdas (`x => x * 2`) y funciones como valores; genéricos solo para las colecciones
  integradas, los de usuario quedan para después.
- Métodos sobre tipos básicos (`longitud`, `mayusculas`, `contiene`, `redondear`) como fachada
  sobre `System.String` y `System.Math`.

## Fase 4: interoperabilidad con .NET y paquetes

La fase técnica más dura del proyecto y la que más devuelve: a partir de aquí cada librería de
.NET es un `importar`, no un proyecto.

- `importar System.IO;` y uso directo de cualquier tipo de .NET, con resolución de sobrecargas
  y de genéricos leyendo los metadatos de los ensamblados de referencia.
- Un archivo de proyecto mínimo (`lince.json`) con nombre, versión, archivos y paquetes
  NuGet; `lince restaurar` que los descarga.
- Varios archivos por programa: `modulo` e `importar` entre módulos propios.
- Emitir contra ensamblados de referencia en vez de contra `System.Private.CoreLib` del
  runtime del compilador, que es lo que se hace hoy; es lo que permite apuntar a un `net9.0` o
  `net10.0` concreto y publicar.
- `leer()` y argumentos de `principal` como primer uso real de la interoperabilidad.

## Fase 5: librería estándar en español

Fachadas finas sobre las librerías de .NET, para que un programa se lea en Lince y no en C#:

- `Web`: `Web.aplicacion()`, `app.ruta("/", ...)`, `app.correr(8080)` sobre ASP.NET Core
  (API mínima); respuestas JSON desde objetos de Lince.
- `Datos`: tablas (`Tabla.desdeCsv`, `filtrar`, `agrupar`, `promedio`) sobre
  `Microsoft.Data.Analysis` y MathNet; `Grafico` sobre ScottPlot.
- `Archivos`, `Http`, `Json`, `Fecha`.
- Escritorio: evaluar Avalonia frente a WinForms cuando llegue el momento.

## Fase 6: asincronía, errores y herencia

Una app web real necesita las tres.

- `intentar { } capturar (e: Error) { }` y `lanzar` sobre las excepciones de .NET.
- `asincrono`/`esperar` sobre `Task`, para que las rutas web y las lecturas de archivos no
  bloqueen.
- Herencia simple e interfaces, necesarias para implementar contratos de .NET.

## Fase 7: REPL y cuadernos

`lince repl` para explorar datos como en un cuaderno de Jupyter: cada línea se compila a un
ensamblado en memoria, lo que ya sabemos hacer. Integración con cuadernos de VS Code más adelante.

## Fase 8: herramientas

- **Servidor de lenguaje (LSP)** y extensión para VS Code: resaltado, errores al escribir,
  ir a la definición, autocompletado. El árbol sintáctico conserva todos los tokens y las
  posiciones pensando en esto.
- **Formateador** (`lince formatear`).
- **Diagnósticos con sugerencias** («¿quiso decir 'contador'?») y advertencias (código
  inalcanzable, variable sin usar).
- **Publicación** como ejecutable autocontenido (`lince publicar`) usando el SDK de .NET.

## Fase 9: lo que hace de un lenguaje una herramienta de trabajo

- Tipo de precisión decimal para dinero (`dinero` sobre `System.Decimal`), porque `decimal`
  es `double` y en facturación eso no sirve.
- Sobrecarga de funciones y parámetros con valor por defecto.
- `segun`/`caso` (switch) con comprobación de exhaustividad para enumeraciones.
- Genéricos de usuario.

## Decisiones que ya se tomaron y conviene no reabrir sin motivo

- `entero` es de 64 bits y `decimal` es `double`. Si algún día hace falta un entero de 32
  bits, será otro tipo (`entero32`), no un cambio del actual.
- La asignación es una sentencia, no una expresión.
- Los bloques siempre llevan llaves y las sentencias siempre terminan en `;`.
- Las palabras reservadas para las fases siguientes ya están reservadas hoy (`lista`,
  `mapa`, `modulo`, `importar`, `intentar`…), así los programas de hoy no dejan de compilar
  cuando lleguen.
- Sin intérprete: todo corre como IL compilado por el JIT de .NET.
- Las librerías vienen de .NET; Lince pone la sintaxis y las fachadas en español.
