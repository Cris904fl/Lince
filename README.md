# Lince

Un lenguaje de programación de propósito general, con palabras clave en español y sintaxis de
llaves al estilo de C# y Go, que compila a ensamblados .NET. Corre sobre el runtime de .NET,
con su recolector de basura y su JIT, y en fases siguientes tendrá acceso a toda su librería.

```
// fibonacci.ln
funcion fibonacci(n: entero): entero {
    si n < 2 {
        regresar n;
    }
    regresar fibonacci(n - 1) + fibonacci(n - 2);
}

funcion principal() {
    para i en 0..10 {
        imprimir("fib(" + i + ") = " + fibonacci(i));
    }
}
```

```
> lince ejecutar ejemplos/fibonacci.ln
fib(0) = 0
fib(1) = 1
...
fib(9) = 34
```

Hay clases y estructuras con campos, métodos, constructor, `este` y `privado`
([ejemplos/cuentas.ln](ejemplos/cuentas.ln)), y tipos nulables con análisis de flujo: `Nodo?`
puede ser `nulo` y solo se usa como `Nodo` tras comprobarlo
([ejemplos/lista-enlazada.ln](ejemplos/lista-enlazada.ln)). La referencia completa del lenguaje está en
[docs/especificacion.md](docs/especificacion.md) y lo que viene, en
[docs/hoja-de-ruta.md](docs/hoja-de-ruta.md).

## Requisitos

- SDK de .NET 10 o superior. El compilador y los programas que genera apuntan a `net10.0`
  porque en la máquina donde nació solo hay runtime 10; `PersistedAssemblyBuilder`, con el que
  se emite el IL, existe desde .NET 9, así que bajar el `TargetFramework` en
  `Directory.Build.props` a `net9.0` también funciona.

## Compilar el compilador

```powershell
dotnet build Lince.slnx
dotnet test Lince.slnx
```

## Usar el compilador

Desde el repositorio, sin instalar nada:

```powershell
dotnet run --project src/Lince.Cli -- ejecutar ejemplos/hola.ln
dotnet run --project src/Lince.Cli -- compilar ejemplos/primos.ln --salida salida
dotnet salida/primos.dll
```

O instalado como herramienta global de dotnet, para tener el comando `lince`:

```powershell
dotnet pack src/Lince.Cli
dotnet tool install --global --add-source ./nupkg Lince.Cli
lince ejecutar ejemplos/decimales.ln
```

(Para actualizar una instalación previa: `dotnet tool update --global --add-source ./nupkg Lince.Cli`.)

Comandos:

| Comando                                        | Qué hace                                                          |
| ---------------------------------------------- | ----------------------------------------------------------------- |
| `lince compilar archivo.ln [--salida carpeta]` | Genera `archivo.dll` y su `runtimeconfig.json` junto al fuente o en la carpeta dada. |
| `lince ejecutar archivo.ln`                    | Compila a una carpeta temporal y corre el programa.               |
| `lince version`                                | Versión del compilador y del runtime.                             |
| `lince ayuda`                                  | Resumen de uso.                                                   |

Códigos de salida: `0` bien, `1` errores de compilación, `2` uso incorrecto; al ejecutar, el que
devuelva el programa (`principal(): entero`).

Los errores salen con archivo, línea y columna, en el formato que entienden los editores:

```
ejemplos/mal.ln(4,12): error LN3011: no existe una variable llamada 'contadr'
        imprimir(contadr);
                 ^
```

## Cómo está hecho

El compilador sigue la arquitectura de Roslyn, en pequeño. Cada fase recibe el resultado de la
anterior y nunca se detiene en el primer error:

| Fase        | Carpeta en `src/Lince.Compiler` | Entrada → salida                                        |
| ----------- | ------------------------------- | ------------------------------------------------------- |
| Léxico      | `Sintaxis/AnalizadorLexico`     | Texto → tokens con posición                              |
| Sintaxis    | `Sintaxis/AnalizadorSintactico` | Tokens → árbol sintáctico inmutable (descenso recursivo con recuperación de errores) |
| Enlace      | `Enlace/Enlazador`              | Árbol sintáctico → árbol enlazado con símbolos y tipos (tipado estático, inferencia local, análisis de caminos) |
| Emisión     | `Emision/EmisorIl`              | Árbol enlazado → ensamblado .NET con `System.Reflection.Emit` (`PersistedAssemblyBuilder`) |

`Compilador.cs` encadena las fases; `Ejecucion/Ejecutor.cs` corre el ensamblado con `dotnet` en
un proceso aparte. No hay intérprete: lo que corre es siempre IL compilado por el JIT de .NET.

```
Lince/
  src/Lince.Compiler/   biblioteca del compilador
  src/Lince.Cli/        comando `lince`
  tests/Lince.Tests/    pruebas de léxico, sintaxis, enlace y de extremo a extremo
  docs/                 especificación y hoja de ruta
  ejemplos/             programas .ln
```

Las pruebas de extremo a extremo compilan cada programa a un `.dll` real, lo corren con
`dotnet` y comparan la salida: son las que dicen que el lenguaje funciona.

## Convenciones

- Código, comentarios, mensajes y documentación en español. Los comentarios explican el porqué.
- Commits al estilo Conventional Commits en español, sin tildes en el asunto
  (`feat(sintaxis):`, `fix(enlace):`, `docs:`), con cuerpo que explica el porqué.
- Cada cambio al lenguaje actualiza `docs/especificacion.md` en el mismo commit.
- Un código de diagnóstico (`LN1234`) nunca cambia de significado; si un error desaparece,
  su número se retira.
