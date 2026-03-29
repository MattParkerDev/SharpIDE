using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Parses VS 2022 for Windows .vsix packages (ZIP archives).
///
/// Discovery algorithm:
///  1. Read extension.vsixmanifest (XML) for identity + grammar asset paths
///  2. Read *.pkgdef for file extension registrations and grammar directory
///  3. Read LanguageServer/server.json for optional LSP server config
/// </summary>
public static partial class VsixPackageParser
{
    private const string ManifestEntryName = "extension.vsixmanifest";
    private const string VsixManifestNamespace = "http://schemas.microsoft.com/developer/vsx-schema/2011";
    private const string GrammarAssetType = "Microsoft.VisualStudio.TextMate.Grammar";
    private const string LspServerConfigPath = "LanguageServer/server.json";

    public static InstalledExtension Parse(string vsixPath)
    {
        using var zip = ZipFile.OpenRead(vsixPath);
        return ParseFromZip(zip);
    }

    private static InstalledExtension ParseFromZip(ZipArchive zip)
    {
        // Step 1: Parse extension.vsixmanifest
        var manifestEntry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidOperationException($"'{ManifestEntryName}' not found in .vsix — is this a VS 2022 extension?");

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

        return new InstalledExtension
        {
            Id = id,
            Version = version,
            Publisher = publisher,
            DisplayName = displayName,
            ExtractedPath = string.Empty,  // set by ExtensionInstaller after extraction
            Languages = languages,
            Grammars = grammars,
            LanguageServers = serverContributions
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

    private static bool IsGrammarFile(string filename) =>
        filename.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmGrammar", StringComparison.OrdinalIgnoreCase) ||
        filename.EndsWith(".tmGrammar.json", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^;.*$", RegexOptions.Multiline)]
    private static partial Regex CommentLineRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\Languages\\File Extensions\\(\.[a-zA-Z0-9_]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtensionKeyRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\ShellFileAssociations\\(\.[a-zA-Z0-9_]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ShellFileAssociationsRegex();

    [GeneratedRegex(@"\[\$RootKey\$\\TextMate\\Repositories\][^\[]*""[^""]*""\s*=\s*""([^""]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TextMateRepositoryRegex();

    [GeneratedRegex(@"\.(tmLanguage|tmGrammar)(\.json)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TmExtensionRegex();
}
