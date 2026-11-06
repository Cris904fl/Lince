# Especificación del lenguaje Lince

> Versión 0.2 (fase 2: objetos). Este documento es la referencia de la sintaxis y la semántica
> que el compilador implementa hoy. Cada cambio al lenguaje se refleja aquí en el mismo trabajo.

Lince es un lenguaje de propósito general, de tipado estático, con palabras clave en español y
sintaxis de llaves al estilo de C# y Go. Compila a ensamblados .NET (IL), así que corre sobre el
runtime de .NET con su recolector de basura, su JIT y, en fases siguientes, su librería estándar.

Los archivos fuente usan la extensión `.ln` y se codifican en UTF-8.

## 1. Estructura léxica

### 1.1 Espacios y comentarios

Los espacios, tabulaciones y saltos de línea separan tokens y no tienen otro significado. No hay
inserción automática de punto y coma: las sentencias terminan en `;`.

```
// comentario de línea
/* comentario
   de bloque */
```

Los comentarios de bloque no se anidan. Un comentario de bloque sin cerrar es un error (`LN1004`).

### 1.2 Identificadores

Un identificador empieza por una letra (cualquier letra Unicode, incluidas `ñ` y las vocales con
tilde) o `_`, y continúa con letras, dígitos o `_`. Distingue mayúsculas de minúsculas.

```
contador  año  _temporal  totalFacturado
```

### 1.3 Palabras clave

Estas palabras están reservadas y no pueden usarse como identificadores:

| Grupo      | Palabras                                                                   |
| ---------- | -------------------------------------------------------------------------- |
| Declaración| `funcion` `sea` `var` `clase` `estructura` `privado` `publico`             |
| Control    | `si` `sino` `mientras` `para` `en` `regresar` `romper` `continuar`         |
| Objetos    | `nuevo` `este`                                                             |
| Literales  | `verdadero` `falso` `nulo`                                                 |
| Tipos      | `entero` `decimal` `texto` `logico` `nada`                                 |

Además quedan reservadas para fases siguientes, y hoy producen el error `LN2004` si se usan:
`modulo`, `importar`, `segun`, `caso`, `intentar`, `capturar`, `lanzar`, `lista`, `mapa`,
`interfaz`, `enumeracion`, `hereda`, `asincrono`, `esperar`.

### 1.4 Literales

| Tipo      | Forma                                   | Ejemplos                      |
| --------- | --------------------------------------- | ----------------------------- |
| `entero`  | dígitos decimales, `_` como separador   | `0`, `42`, `1_000_000`        |
| `decimal` | dígitos, punto, dígitos                 | `3.14`, `0.5`, `1_000.25`     |
| `texto`   | entre comillas dobles, con escapes      | `"hola"`, `"línea\n"`         |
| `logico`  | palabras clave                          | `verdadero`, `falso`          |
| nulo      | palabra clave                           | `nulo`                        |

Un literal `decimal` exige dígitos a ambos lados del punto (`1.` y `.5` no son válidos), lo que
permite que `0..10` se lea como `0`, `..`, `10`.

Los escapes admitidos dentro de un `texto` son `\n`, `\r`, `\t`, `\0`, `\"` y `\\`. Un escape
desconocido es un error (`LN1002`); un texto sin cerrar antes del fin de línea también (`LN1001`).

Un literal `entero` que no cabe en 64 bits es un error (`LN1003`).

### 1.5 Signos y operadores

```
( ) { } , ; : . .. ?
+ - * / %
= += -= *= /= %=
== != < <= > >=
&& || !
```

## 2. Gramática

Notación EBNF: `{ x }` cero o más veces, `[ x ]` opcional, `|` alternativa.

```ebnf
programa             = { declaracion_funcion | declaracion_tipo } ;

declaracion_funcion  = [ modificador ] "funcion" ( identificador | "nuevo" )
                       "(" [ parametros ] ")" [ ":" tipo ] bloque ;
parametros           = parametro { "," parametro } ;
parametro            = identificador ":" tipo ;
tipo                 = ( "entero" | "decimal" | "texto" | "logico" | "nada" | identificador ) [ "?" ] ;

declaracion_tipo     = ( "clase" | "estructura" ) identificador "{" { miembro } "}" ;
miembro              = campo | declaracion_funcion ;
campo                = [ modificador ] identificador ":" tipo [ "=" expresion ] ";" ;
modificador          = "privado" | "publico" ;

bloque               = "{" { sentencia } "}" ;
sentencia            = declaracion_variable
                     | asignacion
                     | sentencia_si
                     | sentencia_mientras
                     | sentencia_para
                     | sentencia_regresar
                     | "romper" ";"
                     | "continuar" ";"
                     | bloque
                     | expresion ";" ;

declaracion_variable = ( "sea" | "var" ) identificador [ ":" tipo ] "=" expresion ";" ;
asignacion           = objetivo ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) expresion ";" ;
objetivo             = identificador | postfija "." identificador ;
sentencia_si         = "si" expresion bloque [ "sino" ( sentencia_si | bloque ) ] ;
sentencia_mientras   = "mientras" expresion bloque ;
sentencia_para       = "para" identificador "en" expresion ".." expresion bloque ;
sentencia_regresar   = "regresar" [ expresion ] ";" ;

expresion            = o_logico ;
o_logico             = y_logico { "||" y_logico } ;
y_logico             = igualdad { "&&" igualdad } ;
igualdad             = comparacion { ( "==" | "!=" ) comparacion } ;
comparacion          = suma { ( "<" | "<=" | ">" | ">=" ) suma } ;
suma                 = producto { ( "+" | "-" ) producto } ;
producto             = unaria { ( "*" | "/" | "%" ) unaria } ;
unaria               = ( "-" | "!" ) unaria | postfija ;
postfija             = primaria { "(" [ argumentos ] ")" | "." identificador } ;
argumentos           = expresion { "," expresion } ;
primaria             = literal | identificador | "este" | "(" expresion ")"
                     | "nuevo" identificador "(" [ argumentos ] ")" ;
```

Observaciones:

- Las condiciones de `si`, `mientras` y `para` no llevan paréntesis obligatorios y el cuerpo es
  siempre un bloque entre llaves, como en Go. `si (x > 1) { }` también es válido porque los
  paréntesis forman una expresión.
- La asignación es una sentencia, no una expresión: `a = b = 1;` no es válido. Su lado
  izquierdo es una variable o un campo (`cuenta.saldo`, `este.saldo` o `saldo` dentro de un
  método); cualquier otra cosa es el error `LN2006`.
- Los operadores binarios asocian por la izquierda. La precedencia, de menor a mayor:
  `||`, `&&`, `== !=`, `< <= > >=`, `+ -`, `* / %`, unarios `- !`, llamada y acceso a miembro.

## 3. Tipos

| Lince     | .NET            | Descripción                                             |
| --------- | --------------- | ------------------------------------------------------- |
| `entero`  | `System.Int64`  | Entero con signo de 64 bits. La aritmética no comprueba desbordamiento. |
| `decimal` | `System.Double` | Punto flotante de 64 bits (IEEE 754).                   |
| `texto`   | `System.String` | Cadena inmutable Unicode. Nunca es `nulo`; `texto?` sí puede serlo (3.3). |
| `logico`  | `System.Boolean`| `verdadero` o `falso`.                                  |
| `nada`    | `void`          | Solo como tipo de retorno; una función sin `: tipo` regresa `nada`. |

`decimal` se llama así por su uso cotidiano («un número con decimales»), no por `System.Decimal`.
La hoja de ruta contempla un tipo de precisión decimal para dinero.

A estos se suman los tipos que declara el programa: las **clases** (por referencia) y las
**estructuras** (por valor), descritas en la sección 4.4. Se nombran por su identificador.

### 3.1 Conversiones implícitas

Solo existen dos:

- `entero` → `decimal`, en asignaciones, argumentos, valores de retorno y operandos mixtos
  (`2 * 1.5` es `decimal`).
- `T` → `T?`: un valor que nunca es nulo cabe donde se admite nulo. Lo contrario, `T?` → `T`,
  no es una conversión: lo decide el análisis de flujo (3.3).

`nulo` solo cabe en un tipo nulable (`texto?`, `Cuenta?`); en cualquier otro es el error
`LN3027`.

Cualquier otra conversión es un error de tipos. No hay conversión implícita de número a texto
salvo en la concatenación (3.2).

### 3.2 Operadores

| Operador             | Operandos                           | Resultado |
| -------------------- | ----------------------------------- | --------- |
| `+ - * / %`          | `entero`, `entero`                  | `entero`  |
| `+ - * / %`          | `decimal` o `entero` mezclados      | `decimal` |
| `+`                  | `texto` con `texto`, `entero`, `decimal` o `logico` (en cualquier orden) | `texto` (concatenación) |
| `< <= > >=`          | numéricos                           | `logico`  |
| `== !=`              | dos del mismo tipo básico, con o sin `?`; `texto?` con `nulo` | `logico`  |
| `== !=`              | dos referencias de la misma clase (con o sin `?`), o una `Clase?` y `nulo`: compara identidad, no contenido | `logico` |
| `&& \|\|`            | `logico`, `logico`; evaluación en cortocircuito | `logico` |
| `!` unario           | `logico`                            | `logico`  |
| `-` unario           | numérico                            | el mismo  |

La división entera trunca hacia cero y dividir un `entero` entre cero lanza la excepción de .NET
`DivideByZeroException`. `/` entre `decimal` sigue IEEE 754 (`1.0 / 0.0` es infinito).

En la concatenación, los números se convierten con la cultura invariante (`2.5`, no `2,5`), los
lógicos se escriben `verdadero` o `falso` y un `texto?` nulo se escribe vacío. Las estructuras no
se comparan con `==` y ni ellas ni las clases se concatenan ni se imprimen directamente: escriba
un método que las convierta a `texto`.

Comparar con `nulo` una expresión cuyo tipo no lleva `?` es un error (`LN3043`): o sobra la
comparación o falta el `?` en la declaración.

### 3.3 Tipos nulables y análisis de flujo

Un tipo por referencia (`texto` o una clase) seguido de `?` admite además el valor `nulo`:
`texto?`, `Nodo?`. Los tipos por valor (`entero`, `logico`, las estructuras) no admiten `?` en
esta versión (`LN3044`).

Un `T?` no se puede usar donde se espera un `T` (`LN3041`) ni se le pueden leer campos o
llamar métodos (`LN3042`)... salvo en los puntos del cuerpo donde el compilador sabe que no es
nulo. Lo sabe por el **análisis de flujo**:

```
funcion largo(nombre: texto?): entero {
    si nombre == nulo {
        regresar 0;
    }
    // Aquí 'nombre' es texto: la rama de arriba regresó.
    regresar medir(nombre);
}

sea nodo = buscar(30);          // Nodo?
si nodo != nulo {
    imprimir(nodo.valor);       // Nodo
}
si nodo != nulo && nodo.valor > 10 { }     // el && evalúa la derecha solo si la izquierda fue cierta
si nodo == nulo || nodo.valor > 10 { }     // el || evalúa la derecha solo si la izquierda fue falsa
mientras nodo != nulo {
    nodo = nodo.siguiente;      // Nodo? otra vez, hasta la siguiente comprobación
}
```

Reglas:

- Una comprobación `x != nulo` hace que `x` sea `T` dentro del `si` (o del `mientras`) y, si
  es `x == nulo`, dentro del `sino`. Con `!` se invierte; con `&&` y `||` se combina como
  cabe esperar.
- Si una rama de un `si` termina con `regresar` (por todos sus caminos), lo que se sabía en
  la otra rama vale para el resto del cuerpo. Tras un `mientras x == nulo { }` sin `romper`,
  `x` no es nulo.
- `x` puede ser una variable, un parámetro, `este.campo`, un campo nombrado a secas o una
  cadena `a.b.c`. Asignar a `x` (o a cualquier prefijo de la cadena) borra lo que se sabía;
  si lo asignado nunca es nulo (`x = nuevo Nodo(1)`), `x` pasa a saberse no nulo.
- Un ciclo olvida, antes de empezar, todo lo que su cuerpo asigna: en la segunda vuelta la
  variable trae lo que le puso la primera.
- Las llamadas a métodos no invalidan lo sabido sobre los campos: es la misma apuesta que hace
  C#, a cambio de no exigir comprobaciones repetidas.

Un `T?` que se sabe no nulo se puede guardar en una variable `T`, pasar a un parámetro `T` o
regresar de una función `T`.

## 4. Declaraciones

### 4.1 Funciones

```
funcion sumar(a: entero, b: entero): entero {
    regresar a + b;
}

funcion saludar(nombre: texto) {
    imprimir("Hola, " + nombre);
}
```

- Todas las funciones se declaran en el nivel superior del archivo y son visibles en todo el
  archivo, antes o después de su declaración; la recursión directa y mutua funcionan sin más.
- Dos funciones no pueden llamarse igual (`LN3001`). No hay sobrecarga en esta fase.
- Una función con tipo de retorno distinto de `nada` debe regresar un valor por todos los caminos
  (`LN3025`).
- Los parámetros son variables inmutables dentro del cuerpo.

### 4.2 Punto de entrada

Todo programa tiene exactamente una función `principal`, sin parámetros, que regresa `nada` o
`entero` (`LN3003`, `LN3004`). Si regresa `entero`, ese valor es el código de salida del proceso.

```
funcion principal(): entero {
    imprimir("listo");
    regresar 0;
}
```

### 4.3 Variables

`sea` declara una variable que no cambia después de inicializada; `var`, una que puede
reasignarse. Ambas exigen un valor inicial. El tipo puede escribirse o inferirse del valor.

```
sea limite = 10;              // entero, inferido
var acumulado: decimal = 0;   // decimal, el 0 se convierte
var apodo: texto? = nulo;     // texto?, puede ser nulo
var nombre = nulo;            // error LN3029: nulo no basta para inferir; escriba el tipo
```

Una variable existe desde su declaración hasta el cierre del bloque que la contiene. No se
puede declarar una variable con el nombre de otra visible en ese punto (`LN3010`), ni con el de
una función (`LN3026`).

### 4.4 Clases y estructuras

```
estructura Punto {
    x: decimal;
    y: decimal;

    funcion mover(dx: decimal, dy: decimal) {
        x += dx;
        y += dy;
    }
}

clase Cuenta {
    titular: texto;
    privado saldo: decimal = 0;
    movimientos: entero = 0;

    funcion nuevo(titular: texto, inicial: decimal) {
        este.titular = titular;
        saldo = inicial;
    }

    funcion depositar(monto: decimal) {
        saldo += monto;
        movimientos += 1;
    }

    funcion describir(): texto {
        regresar titular + ": " + saldo;
    }
}

funcion principal() {
    sea cuenta = nuevo Cuenta("Ana", 100);
    cuenta.depositar(50);
    imprimir(cuenta.describir());

    var p = nuevo Punto(3, 4);
    p.mover(1, 1);
    imprimir(p.x);
}
```

Un tipo se declara en el nivel superior con `clase` o `estructura` y un nombre único
(`LN3032`). Dentro van **campos** (`nombre: tipo;`, con valor inicial opcional) y
**métodos** (funciones). Dos miembros no pueden llamarse igual (`LN3033`). Los tipos, como
las funciones, se ven en todo el archivo sin importar el orden.

- **Clase o estructura.** Una clase es un tipo por referencia: `sea b = a;` hace que `a` y `b`
  sean el mismo objeto, y `==` compara identidad. Una estructura es un tipo por valor:
  `sea b = a;` copia, y cambiar `b` no toca a `a`. Una clase con `?` admite `nulo` (3.3); una
  estructura, nunca. Se emiten como clases y `struct` de .NET.
- **Construcción.** `nuevo Tipo(argumentos)`. Si el tipo declara `funcion nuevo(...)`, ese es su
  constructor y define los argumentos; si no, los argumentos son los campos **sin** valor
  inicial, en orden de declaración (`nuevo Punto(3, 4)`). Los valores iniciales de los campos se
  asignan antes de correr el cuerpo de `nuevo`. Un constructor no lleva tipo de retorno y solo
  puede haber uno (`LN3038`, `LN3033`). Los tipos básicos no se crean con `nuevo` (`LN3040`).
- **Miembros.** Se leen y escriben con `objeto.campo` y se llaman con `objeto.metodo(...)`.
  Dentro de un método, los campos y métodos del propio tipo se nombran a secas; una variable
  local o un parámetro con el mismo nombre tapa al campo, y entonces se usa `este.campo`.
  `este` solo existe dentro de un método o constructor (`LN3036`). Un miembro inexistente es
  `LN3034`; los tipos básicos no tienen miembros en esta versión.
- **Visibilidad.** Todo es público salvo lo marcado `privado`, que solo se puede usar desde
  dentro del tipo (`LN3035`). `publico` se acepta por claridad. Fuera de un tipo, los
  modificadores son un error (`LN3037`).
- **Inmutabilidad y estructuras.** Un campo de una clase guardada en una variable `sea` puede
  cambiarse: lo inmutable es la referencia, no el objeto. Un campo de una estructura solo puede
  cambiarse si la estructura está en un sitio modificable (una variable `var`, `este`, o un campo
  de algo modificable); si está en una variable `sea`, en un parámetro o es el resultado de una
  llamada, el cambio se haría sobre una copia y se informa (`LN3039`). Por la misma razón, un
  método que modifica una estructura actúa sobre el original cuando esta está en una variable
  `var` o en un campo.
- **Parámetros y retornos.** Los objetos se pasan y se regresan como cualquier otro valor; un
  campo puede ser de un tipo declarado (incluido el propio, en una clase: `siguiente: Nodo?`).

## 5. Sentencias

### 5.1 `si` / `sino`

```
si saldo < 0 {
    imprimir("en rojo");
} sino si saldo == 0 {
    imprimir("en cero");
} sino {
    imprimir("en positivo");
}
```

La condición debe ser `logico` (`LN3017`).

### 5.2 `mientras`

```
var i = 0;
mientras i < 3 {
    imprimir(i);
    i += 1;
}
```

### 5.3 `para`

Recorre un rango de enteros: desde el primer valor, inclusive, hasta el segundo, exclusivo. Los
dos límites se evalúan una sola vez, antes de la primera vuelta, y deben ser `entero` (`LN3028`).
La variable del ciclo es inmutable dentro del cuerpo.

```
para i en 0..5 {
    imprimir(i);   // 0 1 2 3 4
}
```

### 5.4 `romper` y `continuar`

Salen del ciclo más interno o saltan a su siguiente vuelta. Fuera de un ciclo son un error
(`LN3023`). En un `para`, `continuar` avanza la variable antes de volver a comprobar el límite.

### 5.5 `regresar`

Termina la función. Lleva valor si y solo si la función no regresa `nada` (`LN3020`, `LN3021`).

### 5.6 Sentencia de expresión

Solo una llamada (a función o a método) o un `nuevo` pueden usarse como sentencia; cualquier
otra expresión suelta es un error porque no tiene efecto (`LN3024`). El valor que devuelvan se
descarta.

## 6. Funciones integradas

| Función              | Descripción                                                                 |
| -------------------- | --------------------------------------------------------------------------- |
| `imprimir()`         | Escribe una línea vacía en la salida estándar.                              |
| `imprimir(valor)`    | Escribe el valor (de cualquier tipo) y un salto de línea. Los números y lógicos se escriben como en la concatenación. |

No se puede declarar una función con el nombre de una integrada (`LN3005`).

## 7. Diagnósticos

Cada diagnóstico lleva archivo, línea y columna (ambas desde 1), un código y un mensaje en
español, con el formato que usan los compiladores de .NET para que los editores lo reconozcan:

```
ejemplos/mal.ln(4,12): error LN3011: no existe una variable llamada 'contadr'
```

Rangos de códigos: `LN1xxx` léxico, `LN2xxx` sintaxis, `LN3xxx` tipos y enlace, `LN4xxx`
emisión y herramienta.

El analizador sintáctico se recupera de los errores: informa el token esperado, lo da por
presente y sigue en la siguiente sentencia, para que un archivo con varios errores los muestre
todos en una pasada. Los errores de tipos no se informan sobre las partes del árbol que ya
tienen un error de sintaxis.

## 8. Programa completo

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
