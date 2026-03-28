namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Runtime registry mapping file extensions to grammar and language server contributions.
/// Populated at startup from persisted registry, and updated when extensions are installed/uninstalled.
/// Thread-safe for reads; writes happen only on install/uninstall (UI thread).
/// </summary>
public class LanguageExtensionRegistry
{
    // file extension (lowercase, e.g. ".axaml") → grammar contribution
    private readonly Dictionary<string, GrammarContribution> _grammarsByExtension = new();

    // language ID → language server contribution
    private readonly Dictionary<string, LanguageServerContribution> _serversByLanguageId = new();

    // all installed extensions (for UI listing)
    private readonly List<InstalledExtension> _extensions = [];

    public IReadOnlyList<InstalledExtension> GetAllExtensions() => _extensions.AsReadOnly();

    /// <summary>
    /// Returns the grammar for a given file extension (e.g. ".axaml"), or null if none registered.
    /// </summary>
    public GrammarContribution? GetGrammar(string fileExtension)
    {
        var key = fileExtension.ToLowerInvariant();
        _grammarsByExtension.TryGetValue(key, out var grammar);
        return grammar;
    }

    /// <summary>
    /// Returns the language server for a given language ID, or null if none registered.
    /// </summary>
    public LanguageServerContribution? GetLanguageServer(string languageId)
    {
        _serversByLanguageId.TryGetValue(languageId.ToLowerInvariant(), out var server);
        return server;
    }

    /// <summary>
    /// Registers an installed extension. If an extension with the same ID already exists, it is replaced.
    /// If two extensions register the same file extension, the later registration wins.
    /// </summary>
    public void Register(InstalledExtension extension)
    {
        // Remove any previous registration with the same ID
        Unregister(extension.Id);

        _extensions.Add(extension);

        // Index grammars by all file extensions declared in LanguageContributions
        foreach (var lang in extension.Languages)
        {
            // Find matching grammar by language ID
            var grammar = extension.Grammars.FirstOrDefault(g =>
                string.Equals(g.LanguageId, lang.LanguageId, StringComparison.OrdinalIgnoreCase));

            if (grammar == null) continue;

            foreach (var ext in lang.FileExtensions)
            {
                var key = ext.ToLowerInvariant();
                if (_grammarsByExtension.ContainsKey(key))
                {
                    // Later installation wins; log handled by caller
                }
                _grammarsByExtension[key] = grammar;
            }
        }

        // Index language servers
        foreach (var server in extension.LanguageServers)
        {
            _serversByLanguageId[server.LanguageId.ToLowerInvariant()] = server;
        }
    }

    /// <summary>
    /// Unregisters all contributions of an installed extension by ID.
    /// </summary>
    public void Unregister(string extensionId)
    {
        var existing = _extensions.FirstOrDefault(e =>
            string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase));

        if (existing == null) return;

        _extensions.Remove(existing);

        // Remove grammar mappings contributed by this extension
        foreach (var lang in existing.Languages)
        {
            var grammar = existing.Grammars.FirstOrDefault(g =>
                string.Equals(g.LanguageId, lang.LanguageId, StringComparison.OrdinalIgnoreCase));
            if (grammar == null) continue;

            foreach (var ext in lang.FileExtensions)
            {
                var key = ext.ToLowerInvariant();
                if (_grammarsByExtension.TryGetValue(key, out var registered) &&
                    registered.GrammarFilePath == grammar.GrammarFilePath)
                {
                    _grammarsByExtension.Remove(key);
                }
            }
        }

        // Remove server mappings
        foreach (var server in existing.LanguageServers)
        {
            _serversByLanguageId.Remove(server.LanguageId.ToLowerInvariant());
        }
    }
}
