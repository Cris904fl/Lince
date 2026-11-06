using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Sintaxis;

/// <summary>
/// Convierte el contenido de un archivo en tokens con su ubicación. No se detiene ante un
/// error: produce un token <see cref="TipoToken.Invalido"/>, informa y sigue, para que el
/// resto del archivo también se analice.
/// </summary>
public sealed class AnalizadorLexico
{
    private readonly ArchivoFuente _archivo;
    private readonly BolsaDiagnosticos _diagnosticos;
    private int _posicion;

    public AnalizadorLexico(ArchivoFuente archivo, BolsaDiagnosticos diagnosticos)
    {
        _archivo = archivo;
        _diagnosticos = diagnosticos;
    }

    /// <summary>Todos los tokens del archivo; el último siempre es <see cref="TipoToken.FinDeArchivo"/>.</summary>
    public static ImmutableArray<Token> Tokenizar(ArchivoFuente archivo, BolsaDiagnosticos diagnosticos)
    {
        var lexico = new AnalizadorLexico(archivo, diagnosticos);
        var tokens = ImmutableArray.CreateBuilder<Token>();
        Token token;
        do
        {
            token = lexico.SiguienteToken();
            tokens.Add(token);
        }
        while (token.Tipo != TipoToken.FinDeArchivo);

        return tokens.ToImmutable();
    }

    private char Actual => Mirar(0);

    private char Siguiente => Mirar(1);

    private char Mirar(int desplazamiento)
    {
        var indice = _posicion + desplazamiento;
        return indice < _archivo.Longitud ? _archivo[indice] : '\0';
    }

    public Token SiguienteToken()
    {
        SaltarEspaciosYComentarios();

        var inicio = _posicion;
        if (_posicion >= _archivo.Longitud)
        {
            return Crear(TipoToken.FinDeArchivo, inicio);
        }

        var c = Actual;

        if (char.IsLetter(c) || c == '_')
        {
            return LeerIdentificadorOPalabra(inicio);
        }

        if (char.IsDigit(c))
        {
            return LeerNumero(inicio);
        }

        if (c == '"')
        {
            return LeerTexto(inicio);
        }

        return LeerSigno(inicio);
    }

    private void SaltarEspaciosYComentarios()
    {
        while (_posicion < _archivo.Longitud)
        {
            if (char.IsWhiteSpace(Actual))
            {
                _posicion++;
            }
            else if (Actual == '/' && Siguiente == '/')
            {
                while (_posicion < _archivo.Longitud && Actual != '\n' && Actual != '\r')
                {
                    _posicion++;
                }
            }
            else if (Actual == '/' && Siguiente == '*')
            {
                var inicio = _posicion;
                _posicion += 2;
                var cerrado = false;
                while (_posicion < _archivo.Longitud)
                {
                    if (Actual == '*' && Siguiente == '/')
                    {
                        _posicion += 2;
                        cerrado = true;
                        break;
                    }

                    _posicion++;
                }

                if (!cerrado)
                {
                    _diagnosticos.Error(Codigos.ComentarioSinCerrar, new Ubicacion(_archivo, inicio, 2),
                        "el comentario de bloque no se cerró con '*/'");
                }
            }
            else
            {
                break;
            }
        }
    }

    private Token LeerIdentificadorOPalabra(int inicio)
    {
        while (_posicion < _archivo.Longitud && (char.IsLetterOrDigit(Actual) || Actual == '_'))
        {
            _posicion++;
        }

        var texto = _archivo.Contenido[inicio.._posicion];
        return Crear(Palabras.TipoDePalabra(texto), inicio);
    }

    private Token LeerNumero(int inicio)
    {
        LeerDigitos();

        var esDecimal = false;
        // Un punto seguido de dígito continúa el número; "1..5" deja el ".." para el rango.
        if (Actual == '.' && char.IsDigit(Siguiente))
        {
            esDecimal = true;
            _posicion++;
            LeerDigitos();
        }

        var texto = _archivo.Contenido[inicio.._posicion];
        var limpio = texto.Replace("_", "");
        var ubicacion = new Ubicacion(_archivo, inicio, _posicion - inicio);

        if (esDecimal)
        {
            if (!double.TryParse(limpio, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var valorDecimal))
            {
                _diagnosticos.Error(Codigos.NumeroFueraDeRango, ubicacion, $"el número '{texto}' no es un decimal válido");
                return new Token(TipoToken.Invalido, texto, null, ubicacion);
            }

            return new Token(TipoToken.LiteralDecimal, texto, valorDecimal, ubicacion);
        }

        if (!long.TryParse(limpio, NumberStyles.None, CultureInfo.InvariantCulture, out var valorEntero))
        {
            _diagnosticos.Error(Codigos.NumeroFueraDeRango, ubicacion,
                $"el número '{texto}' no cabe en un entero de 64 bits (máximo {long.MaxValue})");
            return new Token(TipoToken.Invalido, texto, null, ubicacion);
        }

        return new Token(TipoToken.LiteralEntero, texto, valorEntero, ubicacion);
    }

    private void LeerDigitos()
    {
        while (_posicion < _archivo.Longitud && (char.IsDigit(Actual) || (Actual == '_' && char.IsDigit(Siguiente))))
        {
            _posicion++;
        }
    }

    private Token LeerTexto(int inicio)
    {
        _posicion++; // comilla de apertura
        var valor = new StringBuilder();
        var cerrado = false;

        while (_posicion < _archivo.Longitud)
        {
            var c = Actual;
            if (c == '"')
            {
                _posicion++;
                cerrado = true;
                break;
            }

            if (c == '\n' || c == '\r')
            {
                break;
            }

            if (c == '\\')
            {
                var inicioEscape = _posicion;
                _posicion++;
                var escape = Actual;
                _posicion++;
                switch (escape)
                {
                    case 'n': valor.Append('\n'); break;
                    case 'r': valor.Append('\r'); break;
                    case 't': valor.Append('\t'); break;
                    case '0': valor.Append('\0'); break;
                    case '"': valor.Append('"'); break;
                    case '\\': valor.Append('\\'); break;
                    default:
                        _diagnosticos.Error(Codigos.EscapeDesconocido, new Ubicacion(_archivo, inicioEscape, 2),
                            $"la secuencia de escape '\\{escape}' no existe; las válidas son \\n \\r \\t \\0 \\\" y \\\\");
                        break;
                }

                continue;
            }

            valor.Append(c);
            _posicion++;
        }

        var ubicacion = new Ubicacion(_archivo, inicio, _posicion - inicio);
        if (!cerrado)
        {
            _diagnosticos.Error(Codigos.TextoSinCerrar, ubicacion, "el texto no se cerró con comillas antes del fin de la línea");
        }

        return new Token(TipoToken.LiteralTexto, ubicacion.Texto, valor.ToString(), ubicacion);
    }

    private Token LeerSigno(int inicio)
    {
        var c = Actual;
        var s = Siguiente;
        _posicion++;

        TipoToken tipo;
        switch (c)
        {
            case '(': tipo = TipoToken.ParentesisAbre; break;
            case ')': tipo = TipoToken.ParentesisCierra; break;
            case '{': tipo = TipoToken.LlaveAbre; break;
            case '}': tipo = TipoToken.LlaveCierra; break;
            case ',': tipo = TipoToken.Coma; break;
            case ';': tipo = TipoToken.PuntoYComa; break;
            case ':': tipo = TipoToken.DosPuntos; break;
            case '.' when s == '.': _posicion++; tipo = TipoToken.PuntoPunto; break;
            case '+' when s == '=': _posicion++; tipo = TipoToken.MasIgual; break;
            case '+': tipo = TipoToken.Mas; break;
            case '-' when s == '=': _posicion++; tipo = TipoToken.MenosIgual; break;
            case '-': tipo = TipoToken.Menos; break;
            case '*' when s == '=': _posicion++; tipo = TipoToken.AsteriscoIgual; break;
            case '*': tipo = TipoToken.Asterisco; break;
            case '/' when s == '=': _posicion++; tipo = TipoToken.BarraIgual; break;
            case '/': tipo = TipoToken.Barra; break;
            case '%' when s == '=': _posicion++; tipo = TipoToken.PorcentajeIgual; break;
            case '%': tipo = TipoToken.Porcentaje; break;
            case '=' when s == '=': _posicion++; tipo = TipoToken.IgualIgual; break;
            case '=': tipo = TipoToken.Igual; break;
            case '!' when s == '=': _posicion++; tipo = TipoToken.Distinto; break;
            case '!': tipo = TipoToken.Negacion; break;
            case '<' when s == '=': _posicion++; tipo = TipoToken.MenorIgual; break;
            case '<': tipo = TipoToken.Menor; break;
            case '>' when s == '=': _posicion++; tipo = TipoToken.MayorIgual; break;
            case '>': tipo = TipoToken.Mayor; break;
            case '&' when s == '&': _posicion++; tipo = TipoToken.YLogico; break;
            case '|' when s == '|': _posicion++; tipo = TipoToken.OLogico; break;
            default:
                var ubicacion = new Ubicacion(_archivo, inicio, 1);
                var descripcion = c == '&' ? "'&' (el operador lógico es '&&')"
                    : c == '|' ? "'|' (el operador lógico es '||')"
                    : $"'{c}'";
                _diagnosticos.Error(Codigos.CaracterInesperado, ubicacion, $"carácter inesperado {descripcion}");
                return new Token(TipoToken.Invalido, c.ToString(), null, ubicacion);
        }

        return Crear(tipo, inicio);
    }

    private Token Crear(TipoToken tipo, int inicio)
    {
        var ubicacion = new Ubicacion(_archivo, inicio, _posicion - inicio);
        return new Token(tipo, ubicacion.Texto, null, ubicacion);
    }
}
