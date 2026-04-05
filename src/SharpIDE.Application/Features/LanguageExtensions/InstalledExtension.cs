namespace SharpIDE.Application.Features.LanguageExtensions;

/// <summary>
/// Represents an installed VS Code or Visual Studio language extension (.vsix package).
/// </summary>
public class InstalledExtension
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Publisher { get; init; }
    public required string DisplayName { get; init; }
    public required string ExtractedPath { get; init; } // absolute path to extracted dir
    public ExtensionPackageKind PackageKind { get; init; } = ExtensionPackageKind.VisualStudio;
    public List<LanguageContribution> Languages { get; init; } = [];
    public List<GrammarContribution> Grammars { get; init; } = [];
}

public enum ExtensionPackageKind
{
    VisualStudio = 0,
    VSCode = 1
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
