using System.Collections;
using System.Collections.Immutable;
using Lince.Compiler.Texto;

namespace Lince.Compiler.Diagnosticos;

/// <summary>
/// Acumula los diagnósticos de una fase. Cada fase recibe la bolsa de la anterior, así el
/// resultado final los lista todos en el orden en que aparecieron.
/// </summary>
public sealed class BolsaDiagnosticos : IEnumerable<Diagnostico>
{
    private readonly List<Diagnostico> _diagnosticos = [];

    public int Cantidad => _diagnosticos.Count;

    public bool HayErrores => _diagnosticos.Any(d => d.EsError);

    public void Error(string codigo, Ubicacion ubicacion, string mensaje) =>
        _diagnosticos.Add(new Diagnostico(codigo, Severidad.Error, ubicacion, mensaje));

    public void Advertencia(string codigo, Ubicacion ubicacion, string mensaje) =>
        _diagnosticos.Add(new Diagnostico(codigo, Severidad.Advertencia, ubicacion, mensaje));

    public void AgregarTodos(IEnumerable<Diagnostico> diagnosticos) => _diagnosticos.AddRange(diagnosticos);

    public ImmutableArray<Diagnostico> ToImmutableArray() => [.. _diagnosticos];

    public IEnumerator<Diagnostico> GetEnumerator() => _diagnosticos.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
