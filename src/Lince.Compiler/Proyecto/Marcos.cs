using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Lince.Compiler.Proyecto;

/// <summary>
/// Los marcos compartidos de .NET instalados junto al runtime (<c>Microsoft.AspNetCore.App</c>,
/// <c>Microsoft.WindowsDesktop.App</c>): sus ensamblados se usan para enlazar y el
/// <c>runtimeconfig.json</c> del programa los pide por nombre, sin copiar nada.
/// </summary>
public static class Marcos
{
    public const string NetCore = "Microsoft.NETCore.App";

    /// <summary>La carpeta raíz de la instalación de .NET: la que contiene <c>shared/</c>.</summary>
    public static string RaizDeDotnet =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    /// <summary>
    /// Los ensamblados de un marco compartido, de la versión instalada que coincide con la
    /// mayor del runtime actual (la más alta de esa serie). Vacío si no está instalado.
    /// </summary>
    public static ImmutableArray<string> EnsambladosDe(string marco)
    {
        var carpeta = CarpetaDe(marco);
        return carpeta is null
            ? []
            : [.. Directory.EnumerateFiles(carpeta, "*.dll").Where(r => !Path.GetFileName(r).Contains("Native", StringComparison.Ordinal))];
    }

    public static string? CarpetaDe(string marco)
    {
        var raiz = Path.Combine(RaizDeDotnet, "shared", marco);
        if (!Directory.Exists(raiz))
        {
            return null;
        }

        var mayor = Environment.Version.Major;
        return Directory.EnumerateDirectories(raiz)
            .Select(d => (Carpeta: d, Version: VersionDe(Path.GetFileName(d))))
            .Where(v => v.Version is not null && v.Version.Major == mayor)
            .OrderByDescending(v => v.Version)
            .Select(v => v.Carpeta)
            .FirstOrDefault();
    }

    private static Version? VersionDe(string nombre)
    {
        var fin = nombre.IndexOf('-');
        return Version.TryParse(fin < 0 ? nombre : nombre[..fin], out var version) ? version : null;
    }
}
