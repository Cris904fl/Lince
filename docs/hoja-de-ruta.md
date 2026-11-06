# Hoja de ruta de Lince

Lince se construye por fases. Cada fase deja el compilador usable y con la suite en verde;
nada de una fase se tira en la siguiente. El orden es una propuesta: lo que se necesite antes
sube de posición.

## Fase 1: el núcleo (hecha, 2026-11-06)

Funciones, `entero`, `decimal`, `texto`, `logico`, aritmética, comparaciones, lógicos con
cortocircuito, `si`/`sino`, `mientras`, `para` sobre rangos, `romper`/`continuar`, variables
locales con `sea`/`var` e inferencia, recursión, `imprimir`, concatenación de textos.
Compila a un ensamblado .NET real con `PersistedAssemblyBuilder`. Diagnósticos con posición y
recuperación de errores. CLI `lince compilar` y `lince ejecutar`. Suite de pruebas por fase.

## Fase 2: datos propios

- **Estructuras** (`estructura Punto { x: decimal; y: decimal; }`): tipos por valor, campos,
  constructor posicional, igualdad por valor. Se emiten como `struct` de .NET.
- **Clases** (`clase Cuenta { ... }`): tipos por referencia, campos, métodos, `nuevo`, `este`,
  `publico`/`privado`. Se emiten como clases de .NET. Herencia e interfaces se evalúan
  después; primero composición.
- **Tipos nulables** (`texto?`, `Cuenta?`) con comprobación en el enlazador para que `nulo`
  deje de ser un caso especial de `texto`.
- **Métodos sobre tipos básicos**: `longitud`, `mayusculas`, `contiene`… como fachada sobre
  `System.String`; `redondear`, `absoluto` para números.

## Fase 3: colecciones

- `lista<T>` sobre `List<T>` y `mapa<K, V>` sobre `Dictionary<K, V>`: literales
  (`[1, 2, 3]`, `{"a": 1}`), indexación, `agregar`, `quitar`, `contiene`, `cantidad`.
- `para elemento en coleccion { }` además del rango de enteros.
- Genéricos solo para las colecciones integradas; genéricos de usuario quedan para más adelante.

## Fase 4: módulos y organización

- Varios archivos por programa: `modulo` e `importar`. Cada archivo declara su módulo; el
  compilador recibe una carpeta o una lista de archivos.
- Visibilidad entre módulos (`publico`) y resolución de nombres calificados.
- Un archivo de proyecto mínimo (`lince.json`) con nombre, versión y archivos.

## Fase 5: manejo de errores

- `intentar { } capturar (e: Error) { }` y `lanzar` sobre las excepciones de .NET, para que
  interoperen con el runtime sin inventar un mecanismo paralelo.
- Decidir si se añade un tipo `Resultado<T>` idiomático; primero excepciones, porque es lo
  que la librería de .NET ya usa.

## Fase 6: interoperabilidad con .NET

- Llamar a cualquier tipo de .NET: `importar System.IO;` y usar `Archivo.LeerTexto(...)` o el
  nombre original. Hay que resolver cómo se nombran los miembros (traducción o nombre tal cual).
- Referenciar ensamblados (`--referencia`) y, con ello, emitir contra ensamblados de
  referencia en vez de contra `System.Private.CoreLib` del runtime del compilador, que es lo
  que hace hoy. Es lo que permitirá apuntar a un `net9.0` o `net10.0` concreto.
- `leer()` y argumentos de `principal` como primer uso real de la interoperabilidad.

## Fase 7: herramientas

- **Servidor de lenguaje (LSP)** y extensión para VS Code: resaltado, errores al escribir,
  ir a la definición, autocompletado. El árbol sintáctico ya conserva todos los tokens y las
  posiciones pensando en esto.
- **Formateador** (`lince formatear`): recorre el árbol y reescribe con un estilo fijo.
- **Diagnósticos con sugerencias** («¿quiso decir 'contador'?») y advertencias (código
  inalcanzable, variable sin usar).

## Fase 8: lo que hace de un lenguaje una herramienta de trabajo

- Tipo de precisión decimal para dinero (`dinero` sobre `System.Decimal`), porque `decimal`
  es `double` y en facturación eso no sirve.
- Sobrecarga de funciones y parámetros con valor por defecto.
- `segun`/`caso` (switch) con comprobación de exhaustividad para enumeraciones.
- Funciones como valores y lambdas.
- Publicación como ejecutable autocontenido (`lince publicar`) usando el SDK de .NET.

## Decisiones que ya se tomaron y conviene no reabrir sin motivo

- `entero` es de 64 bits y `decimal` es `double`. Si algún día hace falta un entero de 32
  bits, será otro tipo (`entero32`), no un cambio del actual.
- La asignación es una sentencia, no una expresión.
- Los bloques siempre llevan llaves y las sentencias siempre terminan en `;`.
- Las palabras reservadas para las fases siguientes ya están reservadas hoy (`clase`,
  `lista`, `mapa`, `modulo`, `importar`, `intentar`…), así los programas de la fase 1 no
  dejan de compilar cuando lleguen.
- Sin intérprete: todo corre como IL compilado por el JIT de .NET.
