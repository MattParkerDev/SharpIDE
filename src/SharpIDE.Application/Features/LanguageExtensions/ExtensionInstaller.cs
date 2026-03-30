using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Installs and uninstalls VS Code and Visual Studio language extensions (.vsix packages).
///
/// Install:
///   1. Parse the .vsix via VsixPackageParser
///   2. Extract grammar files + optional LSP server to %APPDATA%/SharpIDE/extensions/<id>/
///   3. Update grammar file paths to absolute paths
///   4. Register in LanguageExtensionRegistry + persist
///
/// Uninstall:
///   1. Unregister from LanguageExtensionRegistry
///   2. Delete the extracted directory
///   3. Persist updated registry
/// </summary>
public class ExtensionInstaller(LanguageExtensionRegistry registry, ILogger<ExtensionInstaller> logger)
{
    private static readonly string[] GrammarExtensions =
        [".tmLanguage", ".tmGrammar", ".tmLanguage.json", ".tmGrammar.json", ".json"];

    private static readonly string[] ServerExtensions =
        [".exe", ".dll", ".js", ".json", ".sh", ".cmd"];

    /// <summary>
    /// Installs a .vsix file. Returns the registered InstalledExtension.
    /// Throws on parse errors; logs and swaps to partial-success on extraction errors.
    /// </summary>
    public InstalledExtension Install(string vsixPath)
    {
        logger.LogInformation("Installing extension from {VsixPath}", vsixPath);

        // 1. Parse metadata (still relative paths inside ZIP)
        var parsed = VsixPackageParser.Parse(vsixPath);

        // 2. Prepare extraction directory
        var extensionsBase = LanguageExtensionPersistence.GetExtensionsBaseDirectory();
        var extractedPath = Path.Combine(extensionsBase, SanitizeId(parsed.Id));
        Directory.CreateDirectory(extractedPath);

        // 3. Extract relevant files from the ZIP
        ExtractFiles(vsixPath, extractedPath, parsed);

        // 4. Resolve grammar file paths to absolute
        var resolvedGrammars = parsed.Grammars
            .Select(g => new GrammarContribution
            {
                LanguageId = g.LanguageId,
                ScopeName = g.ScopeName,
                GrammarFilePath = Path.Combine(extractedPath, NormalizePath(g.GrammarFilePath))
            })
            .Where(g => File.Exists(g.GrammarFilePath))
            .ToList();

        if (resolvedGrammars.Count == 0 && parsed.Grammars.Count > 0)
        {
            // Grammar assets declared but not found after extraction — try scanning for .tmLanguage files
            resolvedGrammars = ScanForGrammars(extractedPath, parsed);
            logger.LogWarning(
                "Grammar assets from manifest not found after extraction for {Id}; found {Count} by scanning",
                parsed.Id, resolvedGrammars.Count);
        }

        if (resolvedGrammars.Count == 0)
        {
            TryDeleteDirectory(extractedPath);
            throw new InvalidOperationException(
                $"'{parsed.DisplayName}' does not contain any importable TextMate syntax files. " +
                "SharpIDE can only import .vsix packages that bundle a TextMate grammar right now.");
        }

        // 5. Resolve language server paths
        var resolvedServers = parsed.LanguageServers
            .Select(s => new LanguageServerContribution
            {
                LanguageId = s.LanguageId,
                Command = Path.Combine(extractedPath, NormalizePath(s.Command)),
                Args = s.Args,
                WorkingDirectory = s.WorkingDirectory,
                TransportType = s.TransportType
            })
            .ToList();

        // 6. Build the final InstalledExtension with absolute paths
        var installed = new InstalledExtension
        {
            Id = parsed.Id,
            Version = parsed.Version,
            Publisher = parsed.Publisher,
            DisplayName = parsed.DisplayName,
            ExtractedPath = extractedPath,
            PackageKind = parsed.PackageKind,
            Languages = parsed.Languages,
            Grammars = resolvedGrammars,
            LanguageServers = resolvedServers
        };

        // 7. Register + persist
        registry.Register(installed);
        LanguageExtensionPersistence.Save(registry.GetAllExtensions());

        logger.LogInformation(
            "Installed '{DisplayName}' ({Id} v{Version}): {GrammarCount} grammar(s), {LangCount} extension(s)",
            installed.DisplayName, installed.Id, installed.Version,
            installed.Grammars.Count, installed.Languages.Sum(l => l.FileExtensions.Length));

        return installed;
    }

    /// <summary>
    /// Uninstalls an extension by ID, removing it from the registry and deleting its extracted files.
    /// </summary>
    public void Uninstall(string extensionId)
    {
        logger.LogInformation("Uninstalling extension {ExtensionId}", extensionId);

        var existing = registry.GetAllExtensions()
            .FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            logger.LogWarning("Extension {ExtensionId} not found; nothing to uninstall", extensionId);
            return;
        }

        registry.Unregister(extensionId);
        LanguageExtensionPersistence.Save(registry.GetAllExtensions());

        if (Directory.Exists(existing.ExtractedPath))
        {
            try
            {
                Directory.Delete(existing.ExtractedPath, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete extension directory {Path}", existing.ExtractedPath);
            }
        }

        logger.LogInformation("Uninstalled {ExtensionId}", extensionId);
    }

    private static void ExtractFiles(string vsixPath, string extractedPath, InstalledExtension parsed)
    {
        using var zip = ZipFile.OpenRead(vsixPath);

        // Collect the set of paths to extract:
        //  - All grammar assets declared in manifest
        //  - All entries in LanguageServer/ directory
        var grammarPaths = new HashSet<string>(
            parsed.Grammars.Select(g => NormalizePath(g.GrammarFilePath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue; // directory entry

            var shouldExtract =
                grammarPaths.Contains(entry.FullName) ||
                entry.FullName.StartsWith("LanguageServer/", StringComparison.OrdinalIgnoreCase) ||
                HasGrammarExtension(entry.Name);

            if (!shouldExtract) continue;

            var destinationPath = Path.Combine(extractedPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static List<GrammarContribution> ScanForGrammars(string directory, InstalledExtension parsed)
    {
        var languageId = parsed.Languages.FirstOrDefault()?.LanguageId ?? "unknown";

        return Directory
            .EnumerateFiles(directory, "*.tmLanguage*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => new GrammarContribution
            {
                LanguageId = languageId,
                GrammarFilePath = f
            })
            .ToList();
    }

    private static bool HasGrammarExtension(string filename) =>
        filename.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmGrammar", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmGrammar.json", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static string SanitizeId(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
