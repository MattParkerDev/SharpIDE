using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Parses VS Code and VS 2022 for Windows .vsix packages (ZIP archives).
///
/// Discovery algorithm:
///  1. Prefer VS Code manifest assets (`extension/package.json`) when present
///  2. Otherwise read extension.vsixmanifest (XML) for identity + grammar asset paths
///  3. Read *.pkgdef for file extension registrations and grammar directory
///  4. Read LanguageServer/server.json for optional LSP server config
/// </summary>
public static partial class VsixPackageParser
{
    private const string ManifestEntryName = "extension.vsixmanifest";
    private const string VsixManifestNamespace = "http://schemas.microsoft.com/developer/vsx-schema/2011";
    private const string GrammarAssetType = "Microsoft.VisualStudio.TextMate.Grammar";
    private const string VsCodeManifestAssetType = "Microsoft.VisualStudio.Code.Manifest";
    private const string LspServerConfigPath = "LanguageServer/server.json";
    private const string DefaultVsCodeManifestPath = "extension/package.json";

    public static InstalledExtension Parse(string vsixPath)
    {
        using var zip = ZipFile.OpenRead(vsixPath);
        return ParseFromZip(zip);
    }

    private static InstalledExtension ParseFromZip(ZipArchive zip)
    {
        var manifestEntry = zip.GetEntry(ManifestEntryName);
        var vsCodeManifestPath = FindVsCodeManifestPath(zip, manifestEntry);
        if (!string.IsNullOrWhiteSpace(vsCodeManifestPath))
            return ParseVsCodeExtension(zip, vsCodeManifestPath);

        // Step 1: Parse extension.vsixmanifest
        manifestEntry ??= zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException($"'{ManifestEntryName}' not found in .vsix — unsupported extension package");

        using var manifestStream = manifestEntry.Open();
        var manifest = XDocument.Load(manifestStream);
        var ns = XNamespace.Get(VsixManifestNamespace);

        var identity = manifest.Descendants(ns + "Identity").FirstOrDefault()
            ?? throw new InvalidOperationException("No <Identity> element found in vsixmanifest");

        var id = identity.Attribute("Id")?.Value ?? throw new InvalidOperationException("Identity missing Id");
        var version = identity.Attribute("Version")?.Value ?? "0.0.0";
        var publisher = identity.Attribute("Publisher")?.Value ?? "Unknown";
        var displayName = manifest.Descendants(ns + "DisplayName").FirstOrDefault()?.Value ?? id;

        // Step 2: Collect grammar asset paths from manifest
        var grammarAssetPaths = manifest
            .Descendants(ns + "Asset")
            .Where(a => a.Attribute("Type")?.Value == GrammarAssetType)
            .Select(a => a.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        // Step 3: Find and parse .pkgdef files
        var pkgdefEntries = zip.Entries
            .Where(e => e.Name.EndsWith(".pkgdef", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var fileExtensions = new List<string>();
        string? grammarDirectory = null;

        foreach (var pkgdefEntry in pkgdefEntries)
        {
            using var pkgdefStream = pkgdefEntry.Open();
            using var reader = new StreamReader(pkgdefStream);
            var pkgdefContent = reader.ReadToEnd();

            fileExtensions.AddRange(ParseFileExtensionsFromPkgdef(pkgdefContent));

            var dir = ParseGrammarDirectoryFromPkgdef(pkgdefContent);
            if (dir != null) grammarDirectory = dir;
        }

        // If .vsixmanifest has grammar assets but no .pkgdef grammar directory,
        // use the directory of the first grammar asset as the grammar folder
        if (grammarDirectory == null && grammarAssetPaths.Count > 0)
        {
            grammarDirectory = Path.GetDirectoryName(grammarAssetPaths[0])?.Replace('\\', '/');
        }

        // Extensions like T4Language declare grammars via pkgdef TextMate\Repositories
        // rather than as explicit manifest Asset elements. In that case scan the grammar
        // directory inside the ZIP to find the actual .tmLanguage / .tmGrammar files.
        if (grammarAssetPaths.Count == 0 && grammarDirectory != null)
        {
            var prefix = grammarDirectory.TrimEnd('/') + "/";
            grammarAssetPaths = zip.Entries
                .Where(e => !e.FullName.EndsWith('/') &&
                            e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            IsGrammarFile(e.Name))
                .Select(e => e.FullName)
                .ToList();
        }

        // Sort so the grammar whose base name matches a pkgdef-discovered extension comes
        // first — making it the "primary" grammar (e.g. t4.tmLanguage before csharp.tmLanguage).
        if (grammarAssetPaths.Count > 1 && fileExtensions.Count > 0)
        {
            var extNames = fileExtensions
                .Select(e => e.TrimStart('.'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            grammarAssetPaths = grammarAssetPaths
                .OrderByDescending(p => extNames.Contains(
                    Path.GetFileNameWithoutExtension(p).Split('.')[0]))
                .ToList();
        }

        // Read fileTypes from the primary grammar to fill extensions omitted from pkgdef.
        // T4Language comments out .tt in ShellFileAssociations because VS owns it natively,
        // but the grammar plist still lists it in its fileTypes array.
        if (grammarAssetPaths.Count > 0)
        {
            var primaryEntry = zip.GetEntry(grammarAssetPaths[0]);
            if (primaryEntry != null)
            {
                using var grammarStream = primaryEntry.Open();
                foreach (var ft in ReadFileTypesFromGrammar(grammarStream, grammarAssetPaths[0]))
                {
                    var dotExt = ft.StartsWith('.') ? ft.ToLowerInvariant() : "." + ft.ToLowerInvariant();
                    if (!fileExtensions.Contains(dotExt, StringComparer.OrdinalIgnoreCase))
                        fileExtensions.Add(dotExt);
                }
            }
        }

        // Build grammar contributions: one per grammar asset path
        // LanguageId derived from grammar filename (e.g. "fsharp.tmLanguage.json" → "fsharp")
        var grammars = grammarAssetPaths
            .Select(path => new GrammarContribution
            {
                LanguageId = DeriveLanguageIdFromPath(path),
                GrammarFilePath = path  // still relative; ExtensionInstaller resolves to absolute
            })
            .ToList();

        // Build language contributions: one per discovered file extension
        // All map to the same language ID (derived from the grammar, or from extension folder name)
        var languages = fileExtensions
            .Select(ext => new LanguageContribution
            {
                LanguageId = grammars.Count > 0 ? grammars[0].LanguageId : ext.TrimStart('.'),
                FileExtensions = [ext]
            })
            .ToList();

        // Step 4: Check for optional LSP server config
        var serverContributions = new List<LanguageServerContribution>();
        var serverConfigEntry = zip.GetEntry(LspServerConfigPath);
        if (serverConfigEntry != null)
        {
            using var serverStream = serverConfigEntry.Open();
            var serverConfig = JsonDocument.Parse(serverStream);
            var serverContrib = ParseServerConfig(serverConfig, languages);
            if (serverContrib != null) serverContributions.Add(serverContrib);
        }

        if (serverContributions.Count == 0)
        {
            serverContributions.AddRange(ParseBundledNodeLanguageServers(zip, languages));
        }

        return new InstalledExtension
        {
            Id = id,
            Version = version,
            Publisher = publisher,
            DisplayName = displayName,
            ExtractedPath = string.Empty,  // set by ExtensionInstaller after extraction
            PackageKind = ExtensionPackageKind.VisualStudio,
            Languages = languages,
            Grammars = grammars,
            LanguageServers = serverContributions
        };
    }

    private static InstalledExtension ParseVsCodeExtension(ZipArchive zip, string packageJsonPath)
    {
        var packageEntry = zip.GetEntry(packageJsonPath)
            ?? throw new InvalidOperationException($"VS Code manifest '{packageJsonPath}' not found in .vsix");

        using var packageStream = packageEntry.Open();
        using var packageDoc = JsonDocument.Parse(packageStream);
        var root = packageDoc.RootElement;

        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("VS Code extension package.json is missing 'name'");

        var publisher = root.TryGetProperty("publisher", out var publisherEl)
            ? publisherEl.GetString()
            : null;
        publisher = !string.IsNullOrWhiteSpace(publisher)
            ? publisher
            : root.TryGetProperty("author", out var authorEl) && authorEl.ValueKind == JsonValueKind.Object &&
              authorEl.TryGetProperty("name", out var authorNameEl)
                ? authorNameEl.GetString()
                : "Unknown";

        var id = !string.IsNullOrWhiteSpace(publisher)
            ? $"{publisher}.{name}"
            : name;

        var displayName = root.TryGetProperty("displayName", out var displayNameEl)
            ? displayNameEl.GetString()
            : null;
        var version = root.TryGetProperty("version", out var versionEl)
            ? versionEl.GetString()
            : null;

        var packageDirectory = Path.GetDirectoryName(packageJsonPath)?.Replace('\\', '/') ?? string.Empty;
        var languages = ParseVsCodeLanguages(root, packageDirectory);
        var grammars = ParseVsCodeGrammars(root, packageDirectory);
        var servers = ParseVsCodeServerPrograms(root, packageDirectory, languages);

        if (languages.Count == 0 && grammars.Count > 0)
        {
            languages = grammars
                .Where(g => !string.IsNullOrWhiteSpace(g.LanguageId))
                .Select(g => new LanguageContribution
                {
                    LanguageId = g.LanguageId,
                    FileExtensions = ReadFileExtensionsFromZipGrammar(zip, g.GrammarFilePath)
                        .Select(ft => ft.StartsWith('.') ? ft.ToLowerInvariant() : "." + ft.ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                })
                .Where(l => l.FileExtensions.Length > 0)
                .ToList();
        }

        return new InstalledExtension
        {
            Id = id!,
            Version = version ?? "0.0.0",
            Publisher = publisher ?? "Unknown",
            DisplayName = displayName ?? name!,
            ExtractedPath = string.Empty,
            PackageKind = ExtensionPackageKind.VSCode,
            Languages = languages,
            Grammars = grammars,
            LanguageServers = servers
        };
    }

    /// <summary>
    /// Parses file extension registrations from .pkgdef content.
    /// Handles two pkgdef conventions (stripping ';' comment lines first):
    ///   [$RootKey$\Languages\File Extensions\.axaml]  — VS language service style
    ///   [$RootKey$\ShellFileAssociations\.t4]          — T4Language / icon-association style
    /// </summary>
    private static IEnumerable<string> ParseFileExtensionsFromPkgdef(string content)
    {
        var uncommented = CommentLineRegex().Replace(content, "");

        foreach (Match m in FileExtensionKeyRegex().Matches(uncommented))
            yield return m.Groups[1].Value.ToLowerInvariant();

        foreach (Match m in ShellFileAssociationsRegex().Matches(uncommented))
            yield return m.Groups[1].Value.ToLowerInvariant();

        foreach (Match m in EditorExtensionRegex().Matches(uncommented))
        {
            var extension = m.Groups[1].Value.ToLowerInvariant();
            yield return extension.StartsWith('.') ? extension : "." + extension;
        }
    }

    /// <summary>
    /// Parses the TextMate grammar directory from .pkgdef content.
    /// Looks for: [$RootKey$\TextMate\Repositories] "Name"="$PackageFolder$\Grammars"
    /// Returns the relative directory path like "Grammars".
    /// </summary>
    private static string? ParseGrammarDirectoryFromPkgdef(string content)
    {
        var match = TextMateRepositoryRegex().Match(content);
        if (!match.Success) return null;

        var rawPath = match.Groups[1].Value;
        // Strip $PackageFolder$\ prefix
        var normalized = rawPath
            .Replace("$PackageFolder$\\", "", StringComparison.OrdinalIgnoreCase)
            .Replace("$PackageFolder$/", "", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');
        return normalized;
    }

    private static string DeriveLanguageIdFromPath(string grammarPath)
    {
        var filename = Path.GetFileName(grammarPath);
        // Strip extensions: "fsharp.tmLanguage.json" → "fsharp"
        // Strip ".tmLanguage.json" or ".tmLanguage" or ".tmGrammar.json"
        return TmExtensionRegex().Replace(filename, "").ToLowerInvariant();
    }

    private static LanguageServerContribution? ParseServerConfig(
        JsonDocument doc, List<LanguageContribution> languages)
    {
        var root = doc.RootElement;
        var command = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(command)) return null;

        var languageId = root.TryGetProperty("language", out var langEl)
            ? langEl.GetString()
            : languages.FirstOrDefault()?.LanguageId;

        if (string.IsNullOrWhiteSpace(languageId)) return null;

        var args = root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array
            ? argsEl.EnumerateArray().Select(a => a.GetString() ?? "").ToArray()
            : [];

        var transport = root.TryGetProperty("transportType", out var transEl)
            ? transEl.GetString() ?? "stdio"
            : "stdio";

        var workingDir = root.TryGetProperty("workingDirectory", out var wdEl)
            ? wdEl.GetString()
            : null;

        return new LanguageServerContribution
        {
            LanguageId = languageId!,
            Command = command!,
            Args = args,
            TransportType = transport,
            WorkingDirectory = workingDir
        };
    }

    private static List<LanguageContribution> ParseVsCodeLanguages(JsonElement root, string packageDirectory)
    {
        if (!TryGetVsCodeContributes(root, out var contributes) ||
            !contributes.TryGetProperty("languages", out var languagesEl) ||
            languagesEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<LanguageContribution>();
        foreach (var languageEl in languagesEl.EnumerateArray())
        {
            if (!languageEl.TryGetProperty("id", out var idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            results.Add(new LanguageContribution
            {
                LanguageId = id!,
                FileExtensions = ReadStringArray(languageEl, "extensions")
                    .Select(NormalizeFileExtension)
                    .Where(static e => !string.IsNullOrWhiteSpace(e))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                FileNames = ReadStringArray(languageEl, "filenames")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                FirstLinePattern = languageEl.TryGetProperty("firstLine", out var firstLineEl)
                    ? firstLineEl.GetString()
                    : null
            });
        }

        return results;
    }

    private static List<GrammarContribution> ParseVsCodeGrammars(JsonElement root, string packageDirectory)
    {
        if (!TryGetVsCodeContributes(root, out var contributes) ||
            !contributes.TryGetProperty("grammars", out var grammarsEl) ||
            grammarsEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<GrammarContribution>();
        foreach (var grammarEl in grammarsEl.EnumerateArray())
        {
            if (!grammarEl.TryGetProperty("path", out var pathEl))
                continue;

            var relativePath = pathEl.GetString();
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var languageId = grammarEl.TryGetProperty("language", out var languageEl)
                ? languageEl.GetString()
                : null;
            var scopeName = grammarEl.TryGetProperty("scopeName", out var scopeEl)
                ? scopeEl.GetString()
                : null;

            results.Add(new GrammarContribution
            {
                LanguageId = !string.IsNullOrWhiteSpace(languageId)
                    ? languageId!
                    : DeriveLanguageIdFromPath(relativePath!),
                ScopeName = scopeName ?? string.Empty,
                GrammarFilePath = CombineZipPath(packageDirectory, relativePath!)
            });
        }

        return results;
    }

    private static List<LanguageServerContribution> ParseVsCodeServerPrograms(
        JsonElement root,
        string packageDirectory,
        List<LanguageContribution> languages)
    {
        if (!TryGetVsCodeContributes(root, out var contributes) ||
            !contributes.TryGetProperty("serverPrograms", out var serversEl) ||
            serversEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<LanguageServerContribution>();
        foreach (var serverEl in serversEl.EnumerateArray())
        {
            if (!serverEl.TryGetProperty("command", out var commandEl))
                continue;

            var command = commandEl.GetString();
            if (string.IsNullOrWhiteSpace(command))
                continue;

            var languageId = serverEl.TryGetProperty("language", out var languageEl)
                ? languageEl.GetString()
                : languages.FirstOrDefault()?.LanguageId;
            if (string.IsNullOrWhiteSpace(languageId))
                continue;

            results.Add(new LanguageServerContribution
            {
                LanguageId = languageId!,
                Command = CombineZipPath(packageDirectory, command!),
                Args = ReadStringArray(serverEl, "args").ToArray(),
                WorkingDirectory = serverEl.TryGetProperty("workingDirectory", out var workingDirEl)
                    ? workingDirEl.GetString()
                    : null,
                TransportType = serverEl.TryGetProperty("transportType", out var transportEl)
                    ? transportEl.GetString() ?? "stdio"
                    : "stdio"
            });
        }

        return results;
    }

    private static List<LanguageServerContribution> ParseBundledNodeLanguageServers(
        ZipArchive zip,
        List<LanguageContribution> languages)
    {
        var results = new List<LanguageServerContribution>();
        var primaryLanguageId = languages.FirstOrDefault()?.LanguageId;
        if (string.IsNullOrWhiteSpace(primaryLanguageId))
            return results;

        var svelteServerEntry = zip.GetEntry("node_modules/svelte-language-server/bin/server.js");
        if (svelteServerEntry != null)
        {
            results.Add(new LanguageServerContribution
            {
                LanguageId = primaryLanguageId!,
                Command = "node_modules/svelte-language-server/bin/server.js",
                Args = ["--stdio"],
                TransportType = "stdio",
                ConfigurationSections = ["svelte", "typescript", "javascript"],
                InitializationOptionsJson = """{"shouldFilterCodeActionKind":true}"""
            });
        }

        return results;
    }

    /// <summary>
    /// Reads the fileTypes array from a TextMate grammar file (plist XML or JSON).
    /// Returns bare extension strings without dots, e.g. "tt", "t4", "ttinclude".
    /// </summary>
    private static IEnumerable<string> ReadFileTypesFromGrammar(Stream stream, string path)
    {
        IEnumerable<string> result;
        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("fileTypes", out var ft) ||
                    ft.ValueKind != JsonValueKind.Array)
                    yield break;
                result = ft.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToArray();
            }
            else // plist XML (.tmLanguage / .tmGrammar)
            {
                var xml = XDocument.Load(stream);
                var fileTypesKey = xml.Descendants("key")
                    .FirstOrDefault(k => k.Value == "fileTypes");
                if (fileTypesKey?.NextNode is not XElement { Name.LocalName: "array" } array)
                    yield break;
                result = array.Elements("string").Select(e => e.Value).ToArray();
            }
        }
        catch
        {
            yield break; // malformed grammar — skip silently
        }

        foreach (var ft in result)
            yield return ft;
    }

    private static bool IsGrammarFile(string filename) =>
        filename.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmGrammar", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmGrammar.json", StringComparison.OrdinalIgnoreCase);

    private static string? FindVsCodeManifestPath(ZipArchive zip, ZipArchiveEntry? manifestEntry)
    {
        if (manifestEntry != null)
        {
            using var manifestStream = manifestEntry.Open();
            var manifest = XDocument.Load(manifestStream);
            var ns = XNamespace.Get(VsixManifestNamespace);
            var path = manifest
                .Descendants(ns + "Asset")
                .FirstOrDefault(a => a.Attribute("Type")?.Value == VsCodeManifestAssetType)
                ?.Attribute("Path")?.Value;
            if (!string.IsNullOrWhiteSpace(path))
                return path!.Replace('\\', '/');
        }

        if (zip.GetEntry(DefaultVsCodeManifestPath) != null)
            return DefaultVsCodeManifestPath;

        return zip.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Select(e => e.FullName.Replace('\\', '/'))
            .FirstOrDefault(p => p.EndsWith("/package.json", StringComparison.OrdinalIgnoreCase) &&
                                 p.StartsWith("extension/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetVsCodeContributes(JsonElement root, out JsonElement contributes)
    {
        if (root.TryGetProperty("contributes", out contributes) &&
            contributes.ValueKind == JsonValueKind.Object)
            return true;

        contributes = default;
        return false;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in arrayEl.EnumerateArray())
        {
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                yield return value!;
        }
    }

    private static string NormalizeFileExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension.ToLowerInvariant()
            : "." + extension.ToLowerInvariant();
    }

    private static string CombineZipPath(string baseDirectory, string relativePath)
    {
        var normalizedRelative = relativePath.Replace('\\', '/');
        if (normalizedRelative.StartsWith("./", StringComparison.Ordinal))
            normalizedRelative = normalizedRelative[2..];

        if (string.IsNullOrWhiteSpace(baseDirectory))
            return normalizedRelative;

        return $"{baseDirectory.TrimEnd('/')}/{normalizedRelative}".TrimStart('/');
    }

    private static IEnumerable<string> ReadFileExtensionsFromZipGrammar(ZipArchive zip, string grammarPath)
    {
        var entry = zip.GetEntry(grammarPath);
        if (entry == null)
            yield break;

        using var stream = entry.Open();
        foreach (var ft in ReadFileTypesFromGrammar(stream, grammarPath))
            yield return ft;
    }

    [GeneratedRegex(@"^;.*$", RegexOptions.Multiline)]
    private static partial Regex CommentLineRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\Languages\\File Extensions\\(\.[a-zA-Z0-9_]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtensionKeyRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\ShellFileAssociations\\(\.[a-zA-Z0-9_]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ShellFileAssociationsRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\Editors\\\{[^}]+\}\\Extensions\][^\[]*^\s*""([^""\\/\r\n]+)""\s*=\s*dword:", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex EditorExtensionRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\TextMate\\Repositories\][^\[]*""[^""]*""\s*=\s*""([^""]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TextMateRepositoryRegex();

    [GeneratedRegex(@"\.(tmLanguage|tmGrammar)(\.json)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TmExtensionRegex();
}
