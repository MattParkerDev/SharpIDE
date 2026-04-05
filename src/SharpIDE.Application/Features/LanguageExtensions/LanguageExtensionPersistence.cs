using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Loads and saves the installed extensions registry to disk.
/// Storage: %APPDATA%/SharpIDE/extensions/registry.json
/// </summary>
public static class LanguageExtensionPersistence
{
    private const string ExtensionsBaseDirectoryOverrideEnvironmentVariable = "SHARPIDE_EXTENSIONS_BASE_DIRECTORY";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string GetRegistryPath()
    {
        return Path.Combine(GetExtensionsBaseDirectory(), "registry.json");
    }

    public static string GetExtensionsBaseDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(ExtensionsBaseDirectoryOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return overrideDirectory;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SharpIDE", "extensions");
    }

    public static List<InstalledExtension> Load()
    {
        var registryPath = GetRegistryPath();
        if (!File.Exists(registryPath)) return [];

        try
        {
            using var stream = File.OpenRead(registryPath);
            return JsonSerializer.Deserialize<List<InstalledExtension>>(stream, JsonOptions) ?? [];
        }
        catch
        {
            // Corrupt registry — start fresh
            return [];
        }
    }

    public static void Save(IReadOnlyList<InstalledExtension> extensions)
    {
        var registryPath = GetRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);

        using var stream = File.Create(registryPath);
        JsonSerializer.Serialize(stream, extensions, JsonOptions);
    }
}
