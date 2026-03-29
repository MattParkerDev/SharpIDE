using AwesomeAssertions;
using SharpIDE.Application.Features.LanguageExtensions;
using Xunit;

namespace SharpIDE.Application.Tests.Features.LanguageExtensions;

/// <summary>
/// Integration tests for VsixPackageParser using the real T4Language.vsix
/// (https://github.com/bricelam/T4Language) as a representative VS for Windows extension.
///
/// T4Language exercises two pkgdef conventions that differ from the "happy path":
///   - Grammars declared via [$RootKey$\TextMate\Repositories] (not manifest Asset elements)
///   - File types declared via [$RootKey$\ShellFileAssociations\.*] (not Languages\File Extensions)
///   - The .tt extension is intentionally commented out (VS owns it natively)
/// </summary>
public class VsixPackageParserTests
{
    private static readonly string VsixPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "T4Language.vsix");

    // ── Metadata ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReturnsCorrectId()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        result.Id.Should().Be("97edd510-988c-473f-9858-ddd5223eab1d");
    }

    [Fact]
    public void Parse_ReturnsCorrectPublisher()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        result.Publisher.Should().Be("Brice Lambson");
    }

    [Fact]
    public void Parse_ReturnsCorrectDisplayName()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        result.DisplayName.Should().Be("T4 Language");
    }

    [Fact]
    public void Parse_ReturnsNonEmptyVersion()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        result.Version.Should().NotBeNullOrEmpty();
    }

    // ── Grammar discovery (via pkgdef TextMate\Repositories) ────────────────

    [Fact]
    public void Parse_FindsT4Grammar()
    {
        var result = VsixPackageParser.Parse(VsixPath);

        result.Grammars.Should().Contain(g =>
            g.GrammarFilePath.EndsWith("t4.tmLanguage", StringComparison.OrdinalIgnoreCase),
            "T4Language bundles Syntaxes/t4.tmLanguage registered via pkgdef TextMate\\Repositories");
    }

    [Fact]
    public void Parse_GrammarsAreNotEmpty()
    {
        var result = VsixPackageParser.Parse(VsixPath);

        result.Grammars.Should().NotBeEmpty(
            "grammar directory 'Syntaxes' is declared in Grammars.pkgdef even though " +
            "the vsixmanifest has no Microsoft.VisualStudio.TextMate.Grammar assets");
    }

    [Fact]
    public void Parse_T4GrammarHasLanguageId()
    {
        var result = VsixPackageParser.Parse(VsixPath);

        var t4Grammar = result.Grammars.FirstOrDefault(g =>
            g.GrammarFilePath.EndsWith("t4.tmLanguage", StringComparison.OrdinalIgnoreCase));

        t4Grammar.Should().NotBeNull();
        t4Grammar!.LanguageId.Should().Be("t4");
    }

    // ── File-extension discovery (via pkgdef ShellFileAssociations) ──────────

    [Fact]
    public void Parse_FindsDotT4Extension()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        var allExtensions = result.Languages.SelectMany(l => l.FileExtensions).ToList();

        allExtensions.Should().Contain(".t4",
            "[$RootKey$\\ShellFileAssociations\\.t4] is present in Grammars.pkgdef");
    }

    [Fact]
    public void Parse_FindsDotTtincludeExtension()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        var allExtensions = result.Languages.SelectMany(l => l.FileExtensions).ToList();

        allExtensions.Should().Contain(".ttinclude",
            "[$RootKey$\\ShellFileAssociations\\.ttinclude] is present in Grammars.pkgdef");
    }

    [Fact]
    public void Parse_FindsDotTtExtension()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        var allExtensions = result.Languages.SelectMany(l => l.FileExtensions).ToList();

        allExtensions.Should().Contain(".tt",
            "t4.tmLanguage's fileTypes plist array includes 'tt' — this fills the gap " +
            "left by the commented-out ;[$RootKey$\\ShellFileAssociations\\.tt] in Grammars.pkgdef");
    }

    // ── No LSP server (T4Language is grammar-only) ───────────────────────────

    [Fact]
    public void Parse_HasNoLanguageServers()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        result.LanguageServers.Should().BeEmpty(
            "T4Language is a grammar-only extension with no bundled language server");
    }
}
