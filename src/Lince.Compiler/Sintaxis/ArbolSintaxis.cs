using System.Collections.Immutable;
using Lince.Compiler.Diagnosticos;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Sintaxis;

/// <summary>
/// El resultado de analizar un archivo: su raíz y los diagnósticos del léxico y la sintaxis.
/// Siempre hay raíz, aunque haya errores; el árbol marca lo que faltó con tokens faltantes.
/// </summary>
public sealed class ArbolSintaxis
{
    private ArbolSintaxis(ArchivoFuente archivo, ProgramaSintaxis raiz, ImmutableArray<Diagnostico> diagnosticos)
    {
        Archivo = archivo;
        Raiz = raiz;
        Diagnosticos = diagnosticos;
    }

    public ArchivoFuente Archivo { get; }

    public ProgramaSintaxis Raiz { get; }

    public ImmutableArray<Diagnostico> Diagnosticos { get; }

    public bool HayErrores => Diagnosticos.Any(d => d.EsError);

    public static ArbolSintaxis Analizar(ArchivoFuente archivo)
    {
        var diagnosticos = new BolsaDiagnosticos();
        var tokens = AnalizadorLexico.Tokenizar(archivo, diagnosticos);
        var raiz = new AnalizadorSintactico(archivo, tokens, diagnosticos).AnalizarPrograma();
        return new ArbolSintaxis(archivo, raiz, diagnosticos.ToImmutableArray());
    }

    public static ArbolSintaxis Analizar(string codigo, string ruta = "<memoria>") =>
        Analizar(ArchivoFuente.DesdeTexto(codigo, ruta));
}
