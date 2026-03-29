# TextMate Theme and Grammar Support in SharpIDE

## Overview

SharpIDE now supports loading custom TextMate themes and syntax grammars from `.vsix` packages, enabling users to extend language support beyond C# and Razor.

---

## Part 1: TextMate Theme Support ✅ IMPLEMENTED

### What It Does

Users can select custom TextMate color themes (`.json` VS Code format or `.tmTheme` plist format) in Settings, and SharpIDE will apply those colors to C# and Razor syntax highlighting.

### Files Created

All files in `src/SharpIDE.Godot/Features/CodeEditor/TextMate/`:

- **`TextMateTheme.cs`** — Data model with longest-prefix-match scope resolution
- **`TextMateThemeParser.cs`** — Parser for `.json` (VS Code) and `.tmTheme` (plist XML) formats
- **`RoslynToTextMateScopes.cs`** — Maps 25+ Roslyn classifications to TextMate scope chains
- **`TextMateEditorThemeColorSetBuilder.cs`** — Builds `EditorThemeColorSet` from parsed theme

### Files Modified

- **`AppState.cs`** — Added `CustomThemePath` property to persist user selection
- **`SharpIdeCodeEdit_Theme.cs`** — Load custom theme on file open; fallback to Light/Dark if unavailable
- **`SettingsWindow.cs`** — Added "Custom TextMate Theme" UI section with file browser

### Usage

1. Open Settings → scroll to "Custom TextMate Theme"
2. Click "Browse…" → select a `.json` (VS Code) or `.tmTheme` (TextMate) file
3. Editor colors update instantly
4. Setting persists in `%APPDATA%/SharpIDE/sharpIde.json`
5. Click "Clear" to return to built-in Light/Dark theme

### Testing

- ✅ Builds without errors
- ✅ Uses only built-in .NET libraries (no new NuGet packages)
- ✅ Persists to config file
- Download test themes: Dracula, Nord, Solarized from VS Code Marketplace

---

## Part 2: VSIX Language Extensions System 🚧 DESIGN COMPLETE

### What It Will Do

SharpIDE will install Visual Studio or VS Code extensions (`.vsix` packages) that contain:
1. **TextMate grammars** for new file types (e.g., `.axaml`, `.gd`, `.rs`)
2. **Language servers** (LSP) for semantic highlighting and future IntelliSense

### Progressive Enhancement Pattern

```
User opens file.axaml
  ↓
1. TextMate grammar tokenizes → colors appear instantly (~5ms)
  ↓
2. Language server launches in parallel (~500ms)
  ↓
3. When LSP ready, semantic tokens replace TextMate → better accuracy
  ↓
4. If LSP fails, TextMate highlighting continues indefinitely
```

### Architecture

#### Application Layer (Platform-Independent)

**New files in `SharpIDE.Application/Features/LanguageExtensions/`:**

1. **`InstalledExtension.cs`** — Models
   - `InstalledExtension` — metadata + contributions
   - `LanguageContribution` — file extension → language ID
   - `GrammarContribution` — language ID → grammar file
   - `LanguageServerContribution` — language ID → LSP server + launch args

2. **`VsixPackageParser.cs`** — Reads `.vsix` ZIP
   - Detects VS Code format (looks for `extension/package.json`)
   - Detects VS 2022 format (looks for `extension.vsixmanifest` + `.pkgdef`)
   - Parses `contributes.languages`, `contributes.grammars`, `contributes.serverPrograms`
   - Uses `System.IO.Compression.ZipFile` (built-in), `System.Text.Json`, `System.Xml.Linq`

3. **`ExtensionInstaller.cs`** — Install/uninstall
   - Extracts grammar and server files to `%APPDATA%/SharpIDE/extensions/<extensionId>/`
   - Registers in `LanguageExtensionRegistry`
   - Persists to `registry.json`

4. **`LanguageExtensionRegistry.cs`** — Runtime mapping
   - Maps file extension → grammar file path
   - Maps language ID → language server binary
   - In-memory cache populated from persisted registry

5. **`LanguageExtensionPersistence.cs`** — Save/load
   - `%APPDATA%/SharpIDE/extensions/registry.json`
   - Uses `System.Text.Json`

6. **`LanguageServerManager.cs`** — LSP lifecycle
   - Launches language servers via subprocess (uses existing `CliWrap` dependency)
   - Sends LSP `textDocument/semanticTokens/full` requests
   - Handles server crashes gracefully
   - Caches one client per language ID

7. **`SemanticTokensManager.cs`** — LSP token resolution
   - Maps LSP token types (`"keyword"`, `"type"`, `"function"`) to Roslyn classifications
   - Returns empty if LSP unavailable (graceful fallback)

#### Godot Layer

**New files in `SharpIDE.Godot/Features/CodeEditor/TextMate/`:**

8. **`GrammarSyntaxHighlighter.cs`** — Godot `SyntaxHighlighter` subclass
   - Uses `TextMateSharp` library for grammar tokenization
   - Phase 1: TextMate tokenizes line → colors via theme
   - Phase 2: LSP semantic tokens arrive → replaces TextMate
   - Per-line render: checks `_semanticTokens` first, falls back to `_grammarTokens`

**New files in `SharpIDE.Godot/Features/ExtensionManager/`:**

9. **`ExtensionManagerPanel.cs`** — UI for extension management
   - List of installed extensions with version, publisher
   - Install button → FileDialog for `.vsix` files
   - Uninstall button per extension
   - Shows which languages/grammars each extension provides

10. **`ExtensionManagerPanel.tscn`** — Godot UI scene

#### Modifications

- **`SharpIdeCodeEdit.cs`** — After Roslyn returns empty spans:
  ```csharp
  if (classifiedSpans.IsEmpty && razorClassifiedSpans.IsEmpty) {
      var grammar = _languageExtensionRegistry.GetGrammar(file.Extension);
      if (grammar != null) {
          _grammarHighlighter.LoadGrammar(grammar.GrammarFilePath, ...);
          SyntaxHighlighter = _grammarHighlighter;
          // Also start LSP if available
          var lsp = await _languageServerManager.GetOrLaunchAsync(grammar.LanguageId);
          _grammarHighlighter.SetSemanticTokenProvider(lsp.GetSemanticTokensAsync);
          return;
      }
  }
  ```

- **`DependencyInjection.cs`** — Register new services as singletons
- **`IdeRoot.cs`** — Load extension registry at startup
- **`SharpIDE.Godot.csproj`** — Add `TextMateSharp` NuGet package v2.0.3

### Why TextMateSharp?

Grammar tokenization requires Oniguruma regex engine (TextMate's regex superset):
- .NET's `Regex` doesn't support all Oniguruma features
- Custom implementation would be ~2000 LOC with many edge cases
- TextMateSharp is MIT, maintained, industry standard (AvaloniaEdit, VS for Mac)
- Native Onigwrap dependency is acceptable for desktop IDE
- Exported apps include native libs alongside binary

### Extension Manifest Format

VS Code or VS 2022 extensions declare contributions in their manifest:

**VS Code (`extension/package.json`):**
```json
{
  "contributes": {
    "languages": [
      {"id": "fsharp", "extensions": [".fs"]}
    ],
    "grammars": [
      {"language": "fsharp", "scopeName": "source.fsharp",
       "path": "./syntaxes/fsharp.tmLanguage.json"}
    ],
    "serverPrograms": [
      {"language": "fsharp", "command": "fsac", "args": ["--stdio"]}
    ]
  }
}
```

**VS 2022 (`extension.vsixmanifest` + `.pkgdef`):**
- `.vsixmanifest` lists grammar files as `<Asset Type="Microsoft.VisualStudio.TextMate.Grammar"/>`
- `.pkgdef` declares file extension → language associations

SharpIDE parses both formats into a unified internal model.

### Real-World Extension Portability

Extension authors add the manifest declaration once — no SharpIDE-specific code needed.

Example: Ionide (F# extension) author adds:
```json
{
  "serverPrograms": [
    {"language": "fsharp", "command": "fsac", "args": ["--stdio"]}
  ]
}
```

SharpIDE reads it, discovers `fsac` binary in the extension, launches it, and routes LSP requests through it automatically.

---

## Implementation Phases

### Phase 1: Grammar-Only Extensions ✅ TextMate complete
- Install `.vsix` → extract grammar → register extension
- Open file → use TextMate highlighting
- No LSP required

### Phase 2: Progressive Enhancement 🚧 Designed
- Detect if extension includes language server
- Launch server in parallel
- Upgrade TextMate → semantic highlighting when ready

### Phase 3: IntelliSense + Navigation 🚧 Future
- Use LSP `textDocument/completion`, `definition`, `hover`, etc.
- Extends beyond syntax highlighting

---

## Data Directory Structure

```
%APPDATA%/SharpIDE/
├── sharpIde.json                 ← main config (CustomThemePath)
├── extensions/
│   ├── registry.json             ← installed extensions list
│   ├── AvaloniaTeam.AvaloniaForVS/
│   │   ├── syntaxes/
│   │   │   └── axaml.tmLanguage.json
│   │   └── extension.json        ← cached manifest
│   ├── ionide.ionide-fsharp/
│   │   ├── syntaxes/
│   │   │   └── fsharp.tmLanguage.json
│   │   ├── bin/
│   │   │   └── fsac             ← extracted LSP server
│   │   └── extension.json
│   └── [more extensions...]
```

---

## Testing Plan

### TextMate Theme (Already Testable)
1. Download `.json` theme: https://marketplace.visualstudio.com/search?target=VSCode&category=Themes (Dracula, Nord, etc.)
2. Open Settings → Custom Theme → Browse → select `.json`
3. Verify C# file colors change
4. Restart app → verify persistence

### VSIX Extensions (When Implemented)
1. Create minimal test `.vsix`:
   - `extension/package.json` with `contributes.languages` + `contributes.grammars`
   - `extension/syntaxes/test.tmLanguage.json` (valid TextMate grammar)
2. Extensions → Install → select test `.vsix`
3. Create `.test` file with sample code
4. Open → verify grammar highlighting
5. Uninstall → verify highlighting removed
6. Restart → verify persistence

### Real Extensions
- Download Avalonia extension for VS from marketplace
- Extract `.vsix`, convert to portable format if needed
- Test `.axaml` file highlighting
- Test with F# extension + fsac language server

---

## Edge Cases & Error Handling

| Scenario | Behavior |
|----------|----------|
| Grammar file corrupt | Log error, skip that grammar, continue with others |
| Language server crash | Log error, revert to TextMate, continue |
| File extension conflicts | Latest-installed extension wins; warn in logs |
| Grammar uses unsupported Oniguruma feature | TextMateSharp throws; catch, disable grammar, log |
| LSP request times out | Ignore timeout, continue with TextMate |
| Server binary not found | Log error, don't launch, use grammar-only |

---

## Dependencies Added

- **TextMateSharp** v2.0.3 (NuGet, in `SharpIDE.Godot.csproj`)
  - Provides TextMate grammar tokenization via Oniguruma regex engine
  - Native Onigwrap binaries included (~4 MB)

All other required libraries are part of .NET BCL:
- `System.IO.Compression.ZipFile` (ZIP reading)
- `System.Text.Json` (JSON parsing)
- `System.Xml.Linq` (XML parsing for plist)
- `CliWrap` (already in project; subprocess management for LSP)

---

## Future Enhancements

1. **IntelliSense** — LSP completion, hover, goto-definition
2. **Refactoring** — LSP codeAction support
3. **Debugging** — Debug Adapter Protocol (DAP) for language servers with debuggers
4. **Extension Settings** — Let extensions register configurable options
5. **Theme Contributions** — Extensions declare custom color themes
6. **Keybinding Contributions** — Extensions define shortcuts
7. **Snippets** — Extensions provide code snippets for new languages

---

## References

- TextMate Scope Naming Convention: https://macromates.com/manual/en/scope_selectors
- Language Server Protocol: https://microsoft.github.io/language-server-protocol/
- VS Code Extension API: https://code.visualstudio.com/api/references/manifest
- TextMateSharp GitHub: https://github.com/danipen/TextMateSharp
- Semantic Tokens in LSP: https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#textDocument_semanticTokens
