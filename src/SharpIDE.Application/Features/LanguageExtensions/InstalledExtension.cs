namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Represents an installed VS 2022 language extension (.vsix package).
/// </summary>
public class InstalledExtension
{
    public required string Id { get; init; }           // e.g. "AvaloniaTeam.AvaloniaForVS"
    public required string Version { get; init; }
    public required string Publisher { get; init; }
    public required string DisplayName { get; init; }
    public required string ExtractedPath { get; init; } // absolute path to extracted dir
    public List<LanguageContribution> Languages { get; init; } = [];
    public List<GrammarContribution> Grammars { get; init; } = [];
    public List<LanguageServerContribution> LanguageServers { get; init; } = [];
}

/// <summary>
/// Associates file extensions (and optional filename/shebang patterns) with a language ID.
/// Discovered from .pkgdef entries like [$RootKey$\Languages\File Extensions\.axaml].
/// </summary>
public class LanguageContribution
{
    public required string LanguageId { get; init; }           // e.g. "axaml"
    public string[] FileExtensions { get; init; } = [];        // e.g. [".axaml"]
    public string[] FileNames { get; init; } = [];             // e.g. ["Makefile"]
    public string? FirstLinePattern { get; init; }             // regex for shebang detection
}

/// <summary>
/// Associates a language ID with a TextMate grammar file.
/// Discovered from vsixmanifest Asset Type="Microsoft.VisualStudio.TextMate.Grammar".
/// </summary>
public class GrammarContribution
{
    public required string LanguageId { get; init; }
    public string ScopeName { get; init; } = string.Empty;
    public required string GrammarFilePath { get; init; }      // absolute path after extraction
}

/// <summary>
/// Describes how to launch an LSP language server bundled with an extension.
/// Discovered from LanguageServer/server.json inside the .vsix.
/// </summary>
public class LanguageServerContribution
{
    public required string LanguageId { get; init; }
    public required string Command { get; init; }              // relative path from ExtractedPath
    public string[] Args { get; init; } = [];                  // e.g. ["--stdio"]
    public string? WorkingDirectory { get; init; }             // relative to ExtractedPath; null = ExtractedPath itself
    public string TransportType { get; init; } = "stdio";      // only "stdio" supported now
}
