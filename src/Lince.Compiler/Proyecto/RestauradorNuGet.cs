using System.Collections.Immutable;
using System.IO.Compression;
using System.Xml.Linq;

namespace Lince.Compiler.Proyecto;

/// <summary>
/// Descarga los paquetes NuGet de un proyecto, con sus dependencias, a una caché local
/// (<c>%LOCALAPPDATA%\lince\paquetes</c>) y devuelve las DLL que el compilador debe
/// referenciar y copiar junto al programa. Habla con nuget.org por su API v3 de contenedor
/// plano, sin más cliente que <c>HttpClient</c>.
/// </summary>
/// <remarks>
/// Es un restaurador pequeño a propósito: resuelve cada dependencia a la versión mínima que
/// pide quien la usa (y la más alta si varios la piden), elige la carpeta <c>lib/</c> del
/// marco más cercano a .NET 10 y da por provistas por el runtime las dependencias
/// <c>System.*</c>, <c>Microsoft.NETCore.*</c> y <c>NETStandard.Library</c>. No maneja
/// paquetes nativos, ni <c>ref/</c>, ni versiones preliminares.
/// </remarks>
public sealed class RestauradorNuGet
{
    private static readonly ImmutableArray<string> MarcosPorPreferencia =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0",
        "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.5", "netstandard1.4", "netstandard1.3",
    ];

    private readonly HttpClient _http;
    private readonly TextWriter _registro;

    public RestauradorNuGet(TextWriter? registro = null, HttpClient? http = null)
    {
        _registro = registro ?? TextWriter.Null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public static string CarpetaDeCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lince", "paquetes");

    public string FuenteDePaquetes { get; init; } = "https://api.nuget.org/v3-flatcontainer/";

    /// <summary>Las DLL de todos los paquetes del proyecto y de sus dependencias, ya en la caché.</summary>
    public async Task<ImmutableArray<string>> RestaurarAsync(Proyecto proyecto, CancellationToken cancelacion = default)
    {
        // Cola de (paquete, versión); si uno aparece dos veces se queda con la versión más alta.
        var pendientes = new Queue<(string Id, Version Version)>();
        var elegidas = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in proyecto.Paquetes)
        {
            pendientes.Enqueue((id, VersionMinima(version)));
        }

        var dlls = new List<string>();
        var procesados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pendientes.Count > 0)
        {
            var (id, version) = pendientes.Dequeue();
            if (elegidas.TryGetValue(id, out var previa) && previa >= version)
            {
                continue;
            }

            elegidas[id] = version;
            var carpeta = await ObtenerAsync(id, version, cancelacion);
            procesados.Add(id);

            foreach (var dependencia in DependenciasDe(carpeta))
            {
                if (!EsDelRuntime(dependencia.Id))
                {
                    pendientes.Enqueue(dependencia);
                }
            }
        }

        // Con las versiones definitivas, las DLL de cada paquete.
        foreach (var (id, version) in elegidas)
        {
            dlls.AddRange(DllsDe(CarpetaDe(id, version)));
        }

        // Si dos paquetes traen la misma DLL se queda la primera.
        return [.. dlls.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];
    }

    /// <summary>Las DLL ya restauradas de un proyecto, sin red; null si falta alguno de sus paquetes en la caché.</summary>
    public static ImmutableArray<string>? YaRestauradas(Proyecto proyecto)
    {
        if (proyecto.Paquetes.Count == 0)
        {
            return [];
        }

        // Sin red no se pueden resolver dependencias nuevas, así que solo vale si cada paquete
        // directo está en la caché; las dependencias se leen de sus nuspec ya descargados.
        var elegidas = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var pendientes = new Queue<(string Id, Version Version)>(proyecto.Paquetes.Select(p => (p.Key, VersionMinima(p.Value))));
        while (pendientes.Count > 0)
        {
            var (id, version) = pendientes.Dequeue();
            if (elegidas.TryGetValue(id, out var previa) && previa >= version)
            {
                continue;
            }

            var carpeta = CarpetaDe(id, version);
            if (!Directory.Exists(carpeta))
            {
                return null;
            }

            elegidas[id] = version;
            foreach (var dependencia in DependenciasDe(carpeta).Where(d => !EsDelRuntime(d.Id)))
            {
                pendientes.Enqueue(dependencia);
            }
        }

        return [.. elegidas.SelectMany(e => DllsDe(CarpetaDe(e.Key, e.Value)))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())];
    }

    private static string CarpetaDe(string id, Version version) =>
        Path.Combine(CarpetaDeCache, id.ToLowerInvariant(), TextoDe(version));

    private async Task<string> ObtenerAsync(string id, Version version, CancellationToken cancelacion)
    {
        var carpeta = CarpetaDe(id, version);
        if (Directory.Exists(carpeta) && Directory.EnumerateFiles(carpeta, "*.nuspec").Any())
        {
            _registro.WriteLine($"  {id} {TextoDe(version)} (en caché)");
            return carpeta;
        }

        var idMinusculas = id.ToLowerInvariant();
        var url = $"{FuenteDePaquetes}{idMinusculas}/{TextoDe(version)}/{idMinusculas}.{TextoDe(version)}.nupkg";
        _registro.WriteLine($"  {id} {TextoDe(version)} (descargando)");

        using var respuesta = await _http.GetAsync(url, cancelacion);
        if (!respuesta.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"No se pudo descargar el paquete '{id}' {TextoDe(version)} de nuget.org ({(int)respuesta.StatusCode}). Revise el nombre y la versión en lince.json.");
        }

        var temporal = Path.Combine(Path.GetTempPath(), "lince", Guid.NewGuid().ToString("N") + ".nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(temporal)!);
        await using (var archivo = File.Create(temporal))
        {
            await respuesta.Content.CopyToAsync(archivo, cancelacion);
        }

        try
        {
            Directory.CreateDirectory(carpeta);
            ZipFile.ExtractToDirectory(temporal, carpeta, overwriteFiles: true);
        }
        finally
        {
            File.Delete(temporal);
        }

        return carpeta;
    }

    /// <summary>Las dependencias del nuspec para el marco más cercano a .NET 10 (o las comunes a todos).</summary>
    private static IEnumerable<(string Id, Version Version)> DependenciasDe(string carpeta)
    {
        var nuspec = Directory.EnumerateFiles(carpeta, "*.nuspec").FirstOrDefault();
        if (nuspec is null)
        {
            yield break;
        }

        var documento = XDocument.Load(nuspec);
        var dependencias = documento.Descendants().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (dependencias is null)
        {
            yield break;
        }

        var grupos = dependencias.Elements().Where(e => e.Name.LocalName == "group").ToList();
        IEnumerable<XElement> elementos;
        if (grupos.Count == 0)
        {
            elementos = dependencias.Elements().Where(e => e.Name.LocalName == "dependency");
        }
        else
        {
            var mejor = grupos
                .Select(g => (Grupo: g, Marco: NormalizarMarco((string?)g.Attribute("targetFramework"))))
                .Where(g => g.Marco is not null)
                .OrderBy(g => MarcosPorPreferencia.IndexOf(g.Marco!))
                .Select(g => g.Grupo)
                .FirstOrDefault()
                ?? grupos.FirstOrDefault(g => g.Attribute("targetFramework") is null);
            elementos = mejor?.Elements().Where(e => e.Name.LocalName == "dependency") ?? [];
        }

        foreach (var elemento in elementos)
        {
            var id = (string?)elemento.Attribute("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                yield return (id, VersionMinima((string?)elemento.Attribute("version") ?? "0.0.0"));
            }
        }
    }

    /// <summary>Las DLL de la carpeta <c>lib/</c> del marco más cercano a .NET 10.</summary>
    private static IEnumerable<string> DllsDe(string carpeta)
    {
        var lib = Path.Combine(carpeta, "lib");
        if (!Directory.Exists(lib))
        {
            return [];
        }

        var mejor = Directory.EnumerateDirectories(lib)
            .Select(d => (Carpeta: d, Marco: NormalizarMarco(Path.GetFileName(d))))
            .Where(d => d.Marco is not null)
            .OrderBy(d => MarcosPorPreferencia.IndexOf(d.Marco!))
            .Select(d => d.Carpeta)
            .FirstOrDefault();
        return mejor is null ? [] : Directory.EnumerateFiles(mejor, "*.dll");
    }

    /// <summary>El nombre corto de un marco (<c>net6.0</c>, <c>netstandard2.0</c>) si es uno que .NET 10 puede cargar; null si no (net461, net6.0-windows).</summary>
    private static string? NormalizarMarco(string? marco)
    {
        if (string.IsNullOrWhiteSpace(marco))
        {
            return null;
        }

        var limpio = marco.Trim().ToLowerInvariant();
        limpio = limpio switch
        {
            ".netstandard2.0" => "netstandard2.0",
            ".netstandard2.1" => "netstandard2.1",
            ".netstandard1.6" => "netstandard1.6",
            ".netstandard1.5" => "netstandard1.5",
            ".netstandard1.4" => "netstandard1.4",
            ".netstandard1.3" => "netstandard1.3",
            ".netcoreapp3.1" => "netcoreapp3.1",
            ".netcoreapp3.0" => "netcoreapp3.0",
            _ => limpio,
        };

        if (limpio.StartsWith("net", StringComparison.Ordinal) && limpio.Length >= 6 && limpio[3] is >= '5' and <= '9' && limpio[4] == '.' && !limpio.Contains('-'))
        {
            return limpio;
        }

        if (limpio.StartsWith("net1", StringComparison.Ordinal) && limpio.Contains('.') && !limpio.Contains('-'))
        {
            return limpio;
        }

        return MarcosPorPreferencia.Contains(limpio) ? limpio : null;
    }

    /// <summary>Paquetes que el runtime ya trae: no se descargan ni se copian.</summary>
    private static bool EsDelRuntime(string id) =>
        id.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
        || id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Microsoft.Bcl.AsyncInterfaces", StringComparison.OrdinalIgnoreCase);

    /// <summary>La versión mínima de un rango NuGet: <c>1.2.3</c>, <c>[1.2.3, )</c>, <c>[1.2.3]</c>, <c>(1.0, 2.0]</c>.</summary>
    public static Version VersionMinima(string rango)
    {
        var texto = rango.Trim().TrimStart('[', '(');
        var coma = texto.IndexOf(',');
        if (coma >= 0)
        {
            texto = texto[..coma];
        }

        texto = texto.TrimEnd(']', ')').Trim();
        var guion = texto.IndexOf('-');
        if (guion >= 0)
        {
            texto = texto[..guion];
        }

        var partes = texto.Split('.');
        var numeros = partes.Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (numeros.Count < 3)
        {
            numeros.Add(0);
        }

        return numeros.Count >= 4 ? new Version(numeros[0], numeros[1], numeros[2], numeros[3]) : new Version(numeros[0], numeros[1], numeros[2]);
    }

    /// <summary>La versión como la nombra nuget.org: sin el cuarto número si es cero.</summary>
    public static string TextoDe(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);
}
