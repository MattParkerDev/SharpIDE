using Microsoft.Extensions.Logging.Abstractions;
using SharpIDE.Application.Features.LanguageExtensions;

namespace SharpIDE.Application.IntegrationTests.Features.LanguageExtensions;

public class VsixPackageParserTests : IDisposable
{
    private const string ExtensionsDirectoryEnvironmentVariable = "SHARPIDE_EXTENSIONS_BASE_DIRECTORY";
    private readonly string? _originalExtensionsDirectory = Environment.GetEnvironmentVariable(ExtensionsDirectoryEnvironmentVariable);
    private readonly string _testExtensionsDirectory = Directory.CreateTempSubdirectory("sharpide-language-extensions-tests-").FullName;

    public VsixPackageParserTests()
    {
        Environment.SetEnvironmentVariable(ExtensionsDirectoryEnvironmentVariable, _testExtensionsDirectory);
    }

    [Fact]
    public void Parse_VisualStudioVsix_FindsExpectedGrammarAndFileExtensions()
    {
        var vsixPath = CreateVisualStudioVsix();

        var result = VsixPackageParser.Parse(vsixPath);

        result.Id.Should().Be("sharpide.testlang");
        result.DisplayName.Should().Be("SharpIDE Test Language");
        result.PackageKind.Should().Be(ExtensionPackageKind.VisualStudio);
        result.Grammars.Should().Contain(g =>
            g.GrammarFilePath.EndsWith("testlang.tmLanguage", StringComparison.OrdinalIgnoreCase));

        var allExtensions = result.Languages.SelectMany(language => language.FileExtensions).ToArray();
        allExtensions.Should().Contain(".testlang");
        allExtensions.Should().Contain(".testlanginc");
        allExtensions.Should().Contain(".testlangextra");
    }

    [Fact]
    public void Parse_VsCodeVsix_FindsLanguageAndGrammarContributions()
    {
        var vsixPath = CreateVsCodeVsix();

        var result = VsixPackageParser.Parse(vsixPath);

        result.Id.Should().Be("trond-snekvik.simple-rst");
        result.PackageKind.Should().Be(ExtensionPackageKind.VSCode);
        result.Languages.Should().ContainSingle(language =>
            language.LanguageId == "restructuredtext" &&
            language.FileExtensions.Contains(".rst"));
        result.Grammars.Should().ContainSingle(grammar =>
            grammar.LanguageId == "restructuredtext" &&
            grammar.ScopeName == "source.rst" &&
            grammar.GrammarFilePath == "extension/syntaxes/rst.tmLanguage.json");
    }

    [Fact]
    public void Install_RegistersGrammarAndResolvesAbsoluteGrammarPath()
    {
        var vsixPath = CreateVisualStudioVsix();
        var registry = new LanguageExtensionRegistry();
        var installer = new ExtensionInstaller(registry, NullLogger<ExtensionInstaller>.Instance);

        var installed = installer.Install(vsixPath);

        installed.ExtractedPath.Should().StartWith(_testExtensionsDirectory);
        installed.Grammars.Should().NotBeEmpty();
        installed.Grammars.Should().OnlyContain(grammar => Path.IsPathRooted(grammar.GrammarFilePath));
        registry.GetGrammar(".testlangextra").Should().NotBeNull();
        File.Exists(registry.GetGrammar(".testlangextra")!.GrammarFilePath).Should().BeTrue();
        File.Exists(Path.Combine(_testExtensionsDirectory, "registry.json")).Should().BeTrue();
    }

    [Fact]
    public void Install_RejectsPackagesWithoutTextMateGrammar()
    {
        var vsixPath = CreateVsCodeVsixWithoutGrammar();
        var registry = new LanguageExtensionRegistry();
        var installer = new ExtensionInstaller(registry, NullLogger<ExtensionInstaller>.Instance);

        var act = () => installer.Install(vsixPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not contain any importable TextMate syntax files*");
        registry.GetAllExtensions().Should().BeEmpty();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ExtensionsDirectoryEnvironmentVariable, _originalExtensionsDirectory);
        if (Directory.Exists(_testExtensionsDirectory))
        {
            Directory.Delete(_testExtensionsDirectory, recursive: true);
        }
    }

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

    private static string CreateVisualStudioVsix()
    {
        var tempDir = Directory.CreateTempSubdirectory("sharpide-visualstudio-vsix-test-");
        var vsixPath = Path.Combine(tempDir.FullName, "testlang.vsix");

        using var archive = System.IO.Compression.ZipFile.Open(vsixPath, System.IO.Compression.ZipArchiveMode.Create);

        var manifestEntry = archive.CreateEntry("extension.vsixmanifest");
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            writer.Write(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                  <Metadata>
                    <Identity Language="en-US" Id="sharpide.testlang" Version="1.0.0" Publisher="SharpIDE Tests" />
                    <DisplayName>SharpIDE Test Language</DisplayName>
                  </Metadata>
                  <Installation>
                    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[17.0, 18.0)" />
                  </Installation>
                  <Assets>
                    <Asset Type="Microsoft.VisualStudio.VsPackage" Path="TestLanguage.pkgdef" />
                  </Assets>
                </PackageManifest>
                """);
        }

        var pkgdefEntry = archive.CreateEntry("TestLanguage.pkgdef");
        using (var writer = new StreamWriter(pkgdefEntry.Open()))
        {
            writer.Write(
                """
                [$RootKey$\TextMate\Repositories]
                "testlang"="$PackageFolder$\Syntaxes"
                [$RootKey$\ShellFileAssociations\.testlang]
                "TestLanguage"=dword:00000064
                [$RootKey$\ShellFileAssociations\.testlanginc]
                "TestLanguage"=dword:00000064
                ;[$RootKey$\ShellFileAssociations\.testlangextra]
                ;"TestLanguage"=dword:00000064
                """);
        }

        var grammarEntry = archive.CreateEntry("Syntaxes/testlang.tmLanguage");
        using (var writer = new StreamWriter(grammarEntry.Open()))
        {
            writer.Write(
                """
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                  <dict>
                    <key>scopeName</key>
                    <string>source.testlang</string>
                    <key>fileTypes</key>
                    <array>
                      <string>testlangextra</string>
                    </array>
                    <key>patterns</key>
                    <array />
                  </dict>
                </plist>
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
}
