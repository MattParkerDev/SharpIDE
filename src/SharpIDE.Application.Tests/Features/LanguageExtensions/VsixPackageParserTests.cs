using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static string CreateVsCodeVsix()
    {
        var tempDir = Directory.CreateTempSubdirectory("sharpide-vscode-vsix-test-");
        var vsixPath = Path.Combine(tempDir.FullName, "simple-rst.vsix");

        using var archive = System.IO.Compression.ZipFile.Open(vsixPath, System.IO.Compression.ZipArchiveMode.Create);

        var manifestEntry = archive.CreateEntry("extension.vsixmanifest");
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            writer.Write(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                  <Metadata>
                    <Identity Language="en-US" Id="simple-rst" Version="1.5.4" Publisher="trond-snekvik" />
                    <DisplayName>reStructuredText Syntax highlighting</DisplayName>
                  </Metadata>
                  <Installation>
                    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
                  </Installation>
                  <Assets>
                    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
                  </Assets>
                </PackageManifest>
                """);
        }

        var packageEntry = archive.CreateEntry("extension/package.json");
        using (var writer = new StreamWriter(packageEntry.Open()))
        {
            writer.Write(
                """
                {
                  "name": "simple-rst",
                  "displayName": "reStructuredText Syntax highlighting",
                  "publisher": "trond-snekvik",
                  "version": "1.5.4",
                  "contributes": {
                    "languages": [
                      {
                        "id": "restructuredtext",
                        "extensions": [".rst"]
                      }
                    ],
                    "grammars": [
                      {
                        "language": "restructuredtext",
                        "scopeName": "source.rst",
                        "path": "./syntaxes/rst.tmLanguage.json"
                      }
                    ]
                  }
                }
                """);
        }

        var grammarEntry = archive.CreateEntry("extension/syntaxes/rst.tmLanguage.json");
        using (var writer = new StreamWriter(grammarEntry.Open()))
        {
            writer.Write(
                """
                {
                  "scopeName": "source.rst",
                  "fileTypes": ["rst"],
                  "patterns": []
                }
                """);
        }

        return vsixPath;
    }

    private static string CreateVsCodeVsixWithoutGrammar()
    {
        var tempDir = Directory.CreateTempSubdirectory("sharpide-vscode-vsix-test-");
        var vsixPath = Path.Combine(tempDir.FullName, "no-grammar.vsix");
        var suffix = Guid.NewGuid().ToString("N");

        using var archive = System.IO.Compression.ZipFile.Open(vsixPath, System.IO.Compression.ZipArchiveMode.Create);

        var manifestEntry = archive.CreateEntry("extension.vsixmanifest");
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            writer.Write(
                $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                  <Metadata>
                    <Identity Language="en-US" Id="no-grammar-{{suffix}}" Version="1.0.0" Publisher="sharpide-tests" />
                    <DisplayName>No Grammar Test</DisplayName>
                  </Metadata>
                  <Installation>
                    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
                  </Installation>
                  <Assets>
                    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
                  </Assets>
                </PackageManifest>
                """);
        }

        var packageEntry = archive.CreateEntry("extension/package.json");
        using (var writer = new StreamWriter(packageEntry.Open()))
        {
            writer.Write(
                $$"""
                {
                  "name": "no-grammar-{{suffix}}",
                  "displayName": "No Grammar Test",
                  "publisher": "sharpide-tests",
                  "version": "1.0.0"
                }
                """);
        }

        return vsixPath;
    }

    private static string CreateVisualStudioSvelteStyleVsix()
    {
        var tempDir = Directory.CreateTempSubdirectory("sharpide-svelte-vsix-test-");
        var vsixPath = Path.Combine(tempDir.FullName, "svelte.vsix");

        using var archive = System.IO.Compression.ZipFile.Open(vsixPath, System.IO.Compression.ZipArchiveMode.Create);

        var manifestEntry = archive.CreateEntry("extension.vsixmanifest");
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            writer.Write(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                  <Metadata>
                    <Identity Language="en-US" Id="SvelteVisualStudio_2022.test" Version="2.2.0" Publisher="Jason Lyu" />
                    <DisplayName>Svelte For Visual Studio</DisplayName>
                  </Metadata>
                  <Installation>
                    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[17.0, 18.0)" />
                  </Installation>
                  <Assets>
                    <Asset Type="Microsoft.VisualStudio.VsPackage" Path="SvelteVisualStudio_2022.pkgdef" />
                  </Assets>
                </PackageManifest>
                """);
        }

        var pkgdefEntry = archive.CreateEntry("SvelteVisualStudio_2022.pkgdef");
        using (var writer = new StreamWriter(pkgdefEntry.Open()))
        {
            writer.Write(
                """
                [$RootKey$\TextMate\Repositories]
                "svelte"="$PackageFolder$\Grammars"
                [$RootKey$\Editors\{91b34873-62ff-42e3-9664-a518b922478f}\Extensions]
                "svelte"=dword:00000064
                """);
        }

        var grammarEntry = archive.CreateEntry("Grammars/svelte.tmLanguage.json");
        using (var writer = new StreamWriter(grammarEntry.Open()))
        {
            writer.Write(
                """
                {
                  "scopeName": "source.svelte",
                  "fileTypes": ["svelte"],
                  "patterns": []
                }
                """);
        }

        var serverEntry = archive.CreateEntry("node_modules/svelte-language-server/bin/server.js");
        using (var writer = new StreamWriter(serverEntry.Open()))
        {
            writer.Write("console.log('svelte test server');");
        }

        return vsixPath;
    }

    // ── Metadata ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReturnsCorrectId()
    {
        var result = VsixPackageParser.Parse(VsixPath);
        result.Id.Should().Be("97edd510-988c-473f-9858-ddd5223eab1d");
        result.PackageKind.Should().Be(ExtensionPackageKind.VisualStudio);
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

    [Fact]
    public void Parse_VsCodePackage_PrefersPackageJsonMetadata()
    {
        var vsixPath = CreateVsCodeVsix();

        var result = VsixPackageParser.Parse(vsixPath);

        result.Id.Should().Be("trond-snekvik.simple-rst");
        result.DisplayName.Should().Be("reStructuredText Syntax highlighting");
        result.Publisher.Should().Be("trond-snekvik");
        result.Version.Should().Be("1.5.4");
        result.PackageKind.Should().Be(ExtensionPackageKind.VSCode);
    }

    [Fact]
    public void Parse_VsCodePackage_FindsLanguageAndGrammarContributions()
    {
        var vsixPath = CreateVsCodeVsix();

        var result = VsixPackageParser.Parse(vsixPath);

        result.Languages.Should().ContainSingle(l =>
            l.LanguageId == "restructuredtext" &&
            l.FileExtensions.Contains(".rst"));

        result.Grammars.Should().ContainSingle(g =>
            g.LanguageId == "restructuredtext" &&
            g.ScopeName == "source.rst" &&
            g.GrammarFilePath == "extension/syntaxes/rst.tmLanguage.json");
    }

    [Fact]
    public void Install_RejectsPackagesWithoutTextMateGrammars()
    {
        var vsixPath = CreateVsCodeVsixWithoutGrammar();
        var registry = new LanguageExtensionRegistry();
        var installer = new ExtensionInstaller(registry, NullLogger<ExtensionInstaller>.Instance);

        var act = () => installer.Install(vsixPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not contain any importable TextMate syntax files*");
        registry.GetAllExtensions().Should().BeEmpty();
    }

    [Fact]
    public void Parse_VisualStudioSvelteStylePackage_FindsBundledNodeLanguageServer()
    {
        var vsixPath = CreateVisualStudioSvelteStyleVsix();

        var result = VsixPackageParser.Parse(vsixPath);

        result.PackageKind.Should().Be(ExtensionPackageKind.VisualStudio);
        result.Languages.Should().ContainSingle(l => l.FileExtensions.Contains(".svelte"));
        result.LanguageServers.Should().ContainSingle(s =>
            s.LanguageId == "svelte" &&
            s.Command == "node_modules/svelte-language-server/bin/server.js");
        result.LanguageServers[0].Args.Should().Equal("--stdio");
    }

    [Fact]
    public void Install_VisualStudioSvelteStylePackage_ExtractsBundledLanguageServerAssets()
    {
        var vsixPath = CreateVisualStudioSvelteStyleVsix();
        var registry = new LanguageExtensionRegistry();
        var installer = new ExtensionInstaller(registry, NullLogger<ExtensionInstaller>.Instance);

        var installed = installer.Install(vsixPath);

        installed.LanguageServers.Should().ContainSingle();
        File.Exists(installed.LanguageServers[0].Command).Should().BeTrue();
    }
}
