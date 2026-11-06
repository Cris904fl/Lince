using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lince.Compiler.Proyecto;

/// <summary>
/// Un proyecto de Lince: la carpeta con un <c>lince.json</c> que dice cómo se llama, qué
/// archivos lo forman, qué paquetes NuGet usa y qué marcos compartidos de .NET necesita.
/// </summary>
/// <example>
/// {
///   "nombre": "inventario",
///   "version": "0.1.0",
///   "archivos": ["principal.ln", "modelos.ln"],
///   "paquetes": { "Newtonsoft.Json": "13.0.3" },
///   "marcos": ["Microsoft.AspNetCore.App"]
/// }
/// </example>
public sealed record Proyecto(
    string Carpeta,
    string Nombre,
    string Version,
    ImmutableArray<string> Archivos,
    ImmutableDictionary<string, string> Paquetes,
    ImmutableArray<string> Marcos)
{
    public const string NombreDeArchivo = "lince.json";

    public string RutaDeArchivo => Path.Combine(Carpeta, NombreDeArchivo);

    /// <summary>Rutas completas de los archivos .ln del proyecto: los listados o, si no hay lista, todos los de la carpeta.</summary>
    public ImmutableArray<string> RutasDeArchivos => Archivos.Length > 0
        ? [.. Archivos.Select(a => Path.GetFullPath(Path.Combine(Carpeta, a)))]
        : [.. Directory.EnumerateFiles(Carpeta, "*.ln", SearchOption.TopDirectoryOnly).OrderBy(r => r, StringComparer.Ordinal)];

    /// <summary>Busca un <c>lince.json</c> en la carpeta dada o en la del archivo dado.</summary>
    public static Proyecto? Buscar(string rutaDeArchivoOCarpeta)
    {
        var carpeta = Directory.Exists(rutaDeArchivoOCarpeta) ? rutaDeArchivoOCarpeta : Path.GetDirectoryName(Path.GetFullPath(rutaDeArchivoOCarpeta));
        if (carpeta is null)
        {
            return null;
        }

        var ruta = Path.Combine(carpeta, NombreDeArchivo);
        return File.Exists(ruta) ? Leer(ruta) : null;
    }

    public static Proyecto Leer(string rutaJson)
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(rutaJson))!;
        var json = JsonSerializer.Deserialize(File.ReadAllText(rutaJson), ContextoJson.Default.ProyectoJson)
            ?? throw new InvalidDataException($"'{rutaJson}' está vacío.");
        return new Proyecto(
            carpeta,
            string.IsNullOrWhiteSpace(json.Nombre) ? Path.GetFileName(carpeta) : json.Nombre,
            string.IsNullOrWhiteSpace(json.Version) ? "0.1.0" : json.Version,
            [.. json.Archivos ?? []],
            (json.Paquetes ?? new Dictionary<string, string>()).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            [.. json.Marcos ?? []]);
    }

    public void Guardar()
    {
        var json = new ProyectoJson
        {
            Nombre = Nombre,
            Version = Version,
            Archivos = [.. Archivos],
            Paquetes = Paquetes.ToDictionary(p => p.Key, p => p.Value),
            Marcos = [.. Marcos],
        };
        File.WriteAllText(RutaDeArchivo, JsonSerializer.Serialize(json, ContextoJson.Default.ProyectoJson));
    }

    /// <summary>Un proyecto nuevo y vacío en una carpeta, con un <c>principal.ln</c> de ejemplo.</summary>
    public static Proyecto Crear(string carpeta, string nombre)
    {
        Directory.CreateDirectory(carpeta);
        var proyecto = new Proyecto(carpeta, nombre, "0.1.0", ["principal.ln"], ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase), []);
        proyecto.Guardar();
        File.WriteAllText(Path.Combine(carpeta, "principal.ln"), $"funcion principal() {{\n    imprimir(\"Hola desde {nombre}\");\n}}\n");
        return proyecto;
    }
}

/// <summary>La forma del archivo <c>lince.json</c> tal como se escribe en disco.</summary>
public sealed class ProyectoJson
{
    [JsonPropertyName("nombre")]
    public string? Nombre { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("archivos")]
    public List<string>? Archivos { get; set; }

    [JsonPropertyName("paquetes")]
    public Dictionary<string, string>? Paquetes { get; set; }

    [JsonPropertyName("marcos")]
    public List<string>? Marcos { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProyectoJson))]
internal sealed partial class ContextoJson : JsonSerializerContext
{
}
