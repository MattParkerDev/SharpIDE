using System.Collections.Generic;
using System.Text.Json;
using System.Xml.Linq;
using Godot;
using Godot.Collections;
using SharpIDE.Application.Features.LanguageExtensions;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace SharpIDE.Godot.Features.CodeEditor.TextMate;

/// <summary>
/// Godot SyntaxHighlighter that tokenizes source text using a TextMate grammar loaded from a
/// VS 2022-style language extension. Supports two phases:
///
///   Phase 1 (TextMate): tokenizes using the loaded grammar file and resolves colors via:
///     - a configured TextMateTheme (custom .json/.tmTheme), or
///     - a built-in scope→color fallback derived from the EditorThemeColorSet.
///
///   Phase 2 (LSP — future): semantic token data from a language server can replace
///   grammar-based highlights when the server signals it is ready.
/// </summary>
public partial class GrammarSyntaxHighlighter : SyntaxHighlighter
{
    private static readonly StringName ColorStringName = "color";
    private readonly Dictionary _emptyDict = new();

    private IGrammar? _grammar;
    private TextMateTheme? _textMateTheme;
    private EditorThemeColorSet _fallbackColorSet = EditorThemeColours.Dark;

    // Pre-computed per-line highlights: line index → (column → Color)
    private readonly System.Collections.Generic.Dictionary<int,
        System.Collections.Generic.Dictionary<int, Color>> _lineHighlights = new();

    public bool IsGrammarLoaded => _grammar != null;

    /// <summary>
    /// Loads the TextMate grammar from the contribution's file path.
    /// Sets <see cref="IsGrammarLoaded"/> to true on success.
    /// </summary>
    public void LoadGrammar(GrammarContribution grammarContribution)
    {
        try
        {
            var options = new SingleFileRegistryOptions(grammarContribution.GrammarFilePath, grammarContribution.ScopeName);
            var registry = new Registry(options);
            _grammar = registry.LoadGrammar(options.ResolvedScopeName);

            if (_grammar == null)
                GD.PrintErr($"[GrammarSyntaxHighlighter] Grammar loaded as null for scope '{options.ResolvedScopeName}'");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GrammarSyntaxHighlighter] Failed to load grammar from '{grammarContribution.GrammarFilePath}': {ex.Message}");
            _grammar = null;
        }
    }

    /// <summary>
    /// Tokenizes all lines of the document and caches per-line color data.
    /// Call this whenever the document text changes.
    /// </summary>
    public void Colorize(string fullText, TextMateTheme? theme, EditorThemeColorSet fallback)
    {
        _textMateTheme = theme;
        _fallbackColorSet = fallback;
        _lineHighlights.Clear();

        if (_grammar == null) return;

        var lines = fullText.Split('\n');
        IStateStack? stateStack = null;

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var lineText = lines[lineIdx];
            // Strip \r for Windows CRLF
            if (lineText.Length > 0 && lineText[^1] == '\r')
                lineText = lineText[..^1];

            try
            {
                var result = _grammar.TokenizeLine(lineText, stateStack, TimeSpan.MaxValue);
                stateStack = result.RuleStack;

                var lineDict = new System.Collections.Generic.Dictionary<int, Color>();
                foreach (var token in result.Tokens)
                {
                    var color = ResolveTokenColor(token.Scopes);
                    if (color.HasValue)
                        lineDict[token.StartIndex] = color.Value;
                }

                if (lineDict.Count > 0)
                    _lineHighlights[lineIdx] = lineDict;
            }
            catch
            {
                // Leave line un-colored on tokenization failure; keep stateStack as-is
            }
        }
    }

    public override Dictionary _GetLineSyntaxHighlighting(int line)
    {
        if (!_lineHighlights.TryGetValue(line, out var lineColors))
            return _emptyDict;

        var result = new Dictionary();
        foreach (var (col, color) in lineColors)
        {
            result[col] = new Dictionary { { ColorStringName, color } };
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Color resolution
    // -------------------------------------------------------------------------

    private Color? ResolveTokenColor(IEnumerable<string> scopes)
    {
        if (_textMateTheme != null)
        {
            // scopes is ordered outermost→innermost; pass reversed (most-specific first)
            return _textMateTheme.ResolveColor(scopes.Reverse());
        }
        return DefaultScopeColorMapper.GetColor(scopes, _fallbackColorSet);
    }

    // -------------------------------------------------------------------------
    // Built-in scope → color fallback (no TextMate theme required)
    // -------------------------------------------------------------------------

    private static class DefaultScopeColorMapper
    {
        /// <summary>
        /// Maps common TextMate scopes to EditorThemeColorSet colors.
        /// Checks from innermost (most-specific) to outermost scope.
        /// </summary>
        public static Color? GetColor(IEnumerable<string> scopes, EditorThemeColorSet colorSet)
        {
            // scopes is ordered outermost→innermost; iterate innermost-first
            foreach (var scope in scopes.Reverse())
            {
                var color = MapScope(scope, colorSet);
                if (color.HasValue) return color;
            }
            return null;
        }

        private static Color? MapScope(string scope, EditorThemeColorSet cs)
        {
            if (scope.StartsWith("comment", StringComparison.Ordinal))               return cs.CommentGreen;
            if (scope.StartsWith("string", StringComparison.Ordinal))                return cs.LightOrangeBrown;
            if (scope.StartsWith("constant.numeric", StringComparison.Ordinal))      return cs.NumberGreen;
            if (scope.StartsWith("constant.character.escape", StringComparison.Ordinal)) return cs.Yellow;
            if (scope.StartsWith("constant.language", StringComparison.Ordinal))     return cs.KeywordBlue;
            if (scope.StartsWith("keyword.operator", StringComparison.Ordinal))      return cs.White;
            if (scope.StartsWith("keyword", StringComparison.Ordinal))               return cs.KeywordBlue;
            if (scope.StartsWith("storage", StringComparison.Ordinal))               return cs.KeywordBlue;
            if (scope.StartsWith("entity.name.type.interface", StringComparison.Ordinal)) return cs.InterfaceGreen;
            if (scope.StartsWith("entity.name.type", StringComparison.Ordinal))      return cs.ClassGreen;
            if (scope.StartsWith("entity.name.function", StringComparison.Ordinal))  return cs.Yellow;
            if (scope.StartsWith("entity.name.namespace", StringComparison.Ordinal)) return cs.White;
            if (scope.StartsWith("entity.name", StringComparison.Ordinal))           return cs.ClassGreen;
            if (scope.StartsWith("support.type", StringComparison.Ordinal))          return cs.ClassGreen;
            if (scope.StartsWith("support.function", StringComparison.Ordinal))      return cs.Yellow;
            if (scope.StartsWith("variable.parameter", StringComparison.Ordinal))    return cs.Gray;
            if (scope.StartsWith("variable.other.constant", StringComparison.Ordinal)) return cs.VariableBlue;
            if (scope.StartsWith("variable", StringComparison.Ordinal))              return cs.VariableBlue;
            if (scope.StartsWith("invalid", StringComparison.Ordinal))               return cs.ErrorRed;
            if (scope.StartsWith("markup.heading", StringComparison.Ordinal))        return cs.KeywordBlue;
            if (scope.StartsWith("markup.bold", StringComparison.Ordinal))           return cs.Yellow;
            if (scope.StartsWith("markup.italic", StringComparison.Ordinal))         return cs.Orange;
            if (scope.StartsWith("punctuation.definition.tag", StringComparison.Ordinal)) return cs.HtmlDelimiterGray;
            if (scope.StartsWith("meta.tag", StringComparison.Ordinal))              return cs.KeywordBlue;
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // IRegistryOptions implementation for a single grammar file
    // -------------------------------------------------------------------------

    private sealed class SingleFileRegistryOptions : IRegistryOptions
    {
        private readonly string _grammarFilePath;

        /// <summary>The scope name resolved from the grammar file or the provided hint.</summary>
        public string ResolvedScopeName { get; }

        public SingleFileRegistryOptions(string grammarFilePath, string? hintScopeName)
        {
            _grammarFilePath = grammarFilePath;
            ResolvedScopeName = !string.IsNullOrWhiteSpace(hintScopeName)
                ? hintScopeName
                : ReadScopeNameFromFile(grammarFilePath) ?? "source.unknown";
        }

        public IRawGrammar GetGrammar(string scopeName)
        {
            if (!string.Equals(scopeName, ResolvedScopeName, StringComparison.OrdinalIgnoreCase))
                return null!;

            try
            {
                using var reader = new StreamReader(_grammarFilePath, System.Text.Encoding.UTF8);
                return GrammarReader.ReadGrammarSync(reader);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SingleFileRegistryOptions] Failed to read grammar '{_grammarFilePath}': {ex.Message}");
                return null!;
            }
        }

        public ICollection<string> GetInjections(string scopeName) => null!;

        public IRawTheme GetTheme(string scopeName) => null!;

        public IRawTheme GetDefaultTheme() => null!;

        // ---------------------------------------------------------------
        // Scope name extraction from grammar file
        // ---------------------------------------------------------------

        private static string? ReadScopeNameFromFile(string filePath)
        {
            try
            {
                var ext = Path.GetExtension(filePath);
                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    return ReadScopeNameFromJson(filePath);

                // plist XML (.tmLanguage / .tmGrammar)
                return ReadScopeNameFromPlist(filePath);
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadScopeNameFromJson(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("scopeName", out var el) ? el.GetString() : null;
        }

        private static string? ReadScopeNameFromPlist(string filePath)
        {
            var xdoc = XDocument.Load(filePath);
            var keys = xdoc.Descendants("key");
            foreach (var key in keys)
            {
                if (key.Value != "scopeName") continue;
                if (key.NextNode is XElement nextEl)
                    return nextEl.Value;
            }
            return null;
        }
    }
}
