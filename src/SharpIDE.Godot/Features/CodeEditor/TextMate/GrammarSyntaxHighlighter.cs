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
/// Visual Studio or VS Code extension and maps scopes onto the IDE's built-in editor palette.
/// </summary>
public partial class GrammarSyntaxHighlighter : SyntaxHighlighter
{
    private const string TextMateTraceEnabledEnvironmentVariable = "SHARPIDE_TEXTMATE_TRACE";
    private const string TextMateTraceFileEnvironmentVariable = "SHARPIDE_TEXTMATE_TRACE_FILE";
    private static readonly StringName ColorStringName = "color";
    private readonly Dictionary _emptyDict = new();

    private IGrammar? _grammar;
    private string _primaryScopeName = "source.unknown";
    private List<CandidateGrammar> _candidateEmbeddedGrammars = [];
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
        Trace($"LoadGrammar path='{grammarContribution.GrammarFilePath}' scopeHint='{grammarContribution.ScopeName}' fileExists={File.Exists(grammarContribution.GrammarFilePath)}");
        try
        {
            var options = new SingleFileRegistryOptions(grammarContribution.GrammarFilePath, grammarContribution.ScopeName);
            Trace($"  resolvedScope='{options.ResolvedScopeName}'");
            var registry = new Registry(options);
            _primaryScopeName = options.ResolvedScopeName;
            _grammar = registry.LoadGrammar(options.ResolvedScopeName);

            if (_grammar == null)
            {
                Trace($"  ERROR: Grammar loaded as null for scope '{options.ResolvedScopeName}'");
                GD.PrintErr($"[GrammarSyntaxHighlighter] Grammar loaded as null for scope '{options.ResolvedScopeName}'");
            }
            else
            {
                Trace("  OK: grammar loaded");
                _candidateEmbeddedGrammars = options.GetSiblingScopeNames()
                    .Where(scopeName => !string.Equals(scopeName, _primaryScopeName, StringComparison.OrdinalIgnoreCase))
                    .Select(scopeName => new CandidateGrammar(scopeName, registry.LoadGrammar(scopeName)))
                    .Where(candidate => candidate.Grammar != null)
                    .ToList()!;
                Trace($"  loaded {_candidateEmbeddedGrammars.Count} sibling grammar candidate(s)");
            }
        }
        catch (Exception ex)
        {
            Trace($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            GD.PrintErr($"[GrammarSyntaxHighlighter] Failed to load grammar from '{grammarContribution.GrammarFilePath}': {ex.Message}");
            _grammar = null;
        }
    }

    /// <summary>
    /// Tokenizes all lines of the document and caches per-line color data.
    /// Call this whenever the document text changes.
    /// </summary>
    public void Colorize(string fullText, EditorThemeColorSet fallback)
    {
        _fallbackColorSet = fallback;
        _lineHighlights.Clear();

        if (_grammar == null) { Trace("Colorize: grammar is null, skipping"); return; }
        Trace($"Colorize: tokenizing {fullText.Split('\n').Length} lines");

        var lines = fullText.Split('\n');
        IStateStack? stateStack = null;
        var candidateStateStacks = _candidateEmbeddedGrammars.ToDictionary(
            candidate => candidate.ScopeName,
            _ => (IStateStack?)null,
            StringComparer.OrdinalIgnoreCase);
        var totalColoredLines = 0;

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

                var bestTokens = result.Tokens;
                var bestScore = ScoreTokens(result.Tokens, _primaryScopeName);

                if (ShouldTrySiblingGrammarFallback(lineText, bestScore))
                {
                    foreach (var candidate in _candidateEmbeddedGrammars)
                    {
                        var candidateResult = candidate.Grammar!.TokenizeLine(lineText, candidateStateStacks[candidate.ScopeName], TimeSpan.MaxValue);
                        candidateStateStacks[candidate.ScopeName] = candidateResult.RuleStack;

                        var candidateScore = ScoreTokens(candidateResult.Tokens, candidate.ScopeName);
                        if (candidateScore > bestScore)
                        {
                            bestTokens = candidateResult.Tokens;
                            bestScore = candidateScore;
                        }
                    }
                }

                var lineDict = new System.Collections.Generic.Dictionary<int, Color>();
                foreach (var token in bestTokens)
                {
                    var color = ResolveTokenColor(token.Scopes);
                    if (color.HasValue)
                        lineDict[token.StartIndex] = color.Value;
                }

                if (lineDict.Count > 0)
                {
                    _lineHighlights[lineIdx] = lineDict;
                    totalColoredLines++;
                }
            }
            catch (Exception ex)
            {
                Trace($"  tokenize exception line {lineIdx}: {ex.GetType().Name}: {ex.Message}");
                // Leave line un-colored on tokenization failure; keep stateStack as-is
            }
        }
        Trace($"  Colorize done: {totalColoredLines}/{lines.Length} lines have color data");
    }

    private static bool ShouldTrySiblingGrammarFallback(string lineText, int primaryScore)
    {
        return primaryScore == 0 && string.IsNullOrWhiteSpace(lineText) is false;
    }

    private static int ScoreTokens(IEnumerable<IToken> tokens, string rootScopeName)
    {
        var score = 0;
        foreach (var token in tokens)
        {
            foreach (var scope in token.Scopes)
            {
                if (string.Equals(scope, rootScopeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (scope.StartsWith("source.", StringComparison.OrdinalIgnoreCase) ||
                    scope.StartsWith("text.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                score++;
            }
        }

        return score;
    }

    private static void Trace(string message)
    {
        if (IsTraceEnabled() is false)
        {
            return;
        }

        var line = $"[TextMate] {message}";
        GD.Print(line);

        var traceFilePath = System.Environment.GetEnvironmentVariable(TextMateTraceFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(traceFilePath))
        {
            return;
        }

        try
        {
            File.AppendAllText(traceFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{System.Environment.NewLine}");
        }
        catch
        {
            // Best-effort file logging only.
        }
    }

    private static bool IsTraceEnabled()
    {
        var value = System.Environment.GetEnvironmentVariable(TextMateTraceEnabledEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
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
            if (scope.StartsWith("entity.other.attribute-name", StringComparison.Ordinal)) return cs.InterfaceGreen;
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
            if (scope.StartsWith("punctuation.section.embedded", StringComparison.Ordinal)) return cs.HtmlDelimiterGray;
            if (scope.StartsWith("punctuation.separator.key-value", StringComparison.Ordinal)) return cs.White;
            if (scope.StartsWith("punctuation.definition.tag", StringComparison.Ordinal)) return cs.HtmlDelimiterGray;
            if (scope.StartsWith("punctuation.definition.string", StringComparison.Ordinal)) return cs.LightOrangeBrown;
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

        public IReadOnlyList<string> GetSiblingScopeNames()
        {
            var directory = Path.GetDirectoryName(_grammarFilePath);
            if (directory == null)
            {
                return [];
            }

            return Directory.EnumerateFiles(directory)
                .Where(IsTmGrammarFile)
                .Select(ReadScopeNameFromFile)
                .Where(scopeName => !string.IsNullOrWhiteSpace(scopeName))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(scopeName => scopeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IRawGrammar GetGrammar(string scopeName)
        {
            // Primary grammar
            if (string.Equals(scopeName, ResolvedScopeName, StringComparison.OrdinalIgnoreCase))
                return ReadGrammarFile(_grammarFilePath);

            // Look for a sibling grammar in the same directory that declares this scope name.
            // This resolves embedded grammars such as source.cs inside T4 templates, where
            // the extension ships csharp.tmLanguage alongside t4.tmLanguage in Syntaxes/.
            var directory = Path.GetDirectoryName(_grammarFilePath);
            if (directory != null)
            {
                foreach (var candidate in Directory.EnumerateFiles(directory).Where(IsTmGrammarFile))
                {
                    var candidateScope = ReadScopeNameFromFile(candidate);
                    if (string.Equals(candidateScope, scopeName, StringComparison.OrdinalIgnoreCase))
                        return ReadGrammarFile(candidate);
                }
            }

            return null!;
        }

        private static IRawGrammar ReadGrammarFile(string filePath)
        {
            try
            {
                // TextMateSharp's GrammarReader only supports JSON-format grammars.
                // XML PList (.tmLanguage/.tmGrammar without .json) must be converted first.
                var isXmlPlist = !filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                var json = isXmlPlist
                    ? ConvertPlistXmlToJson(filePath)
                    : File.ReadAllText(filePath, System.Text.Encoding.UTF8);

                using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
                return GrammarReader.ReadGrammarSync(reader);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SingleFileRegistryOptions] Failed to read grammar '{filePath}': {ex.Message}");
                return null!;
            }
        }

        /// <summary>
        /// Converts a TextMate XML PList grammar file to JSON string that TextMateSharp can parse.
        /// PList types map to JSON: dict→object, array→array, string→string, integer→number,
        /// real→number, true→true, false→false.
        /// </summary>
        private static string ConvertPlistXmlToJson(string filePath)
        {
            var doc = XDocument.Load(filePath);
            // Root is <plist><dict>...</dict></plist>
            var root = doc.Descendants("dict").FirstOrDefault()
                ?? throw new InvalidOperationException("No root <dict> found in PList grammar");

            var sb = new System.Text.StringBuilder();
            WritePlistNode(root, sb);
            return sb.ToString();
        }

        private static void WritePlistNode(XElement element, System.Text.StringBuilder sb)
        {
            switch (element.Name.LocalName)
            {
                case "dict":
                    sb.Append('{');
                    var dictChildren = element.Elements().ToList();
                    var first = true;
                    for (int i = 0; i + 1 < dictChildren.Count; i += 2)
                    {
                        var keyEl = dictChildren[i];
                        var valEl = dictChildren[i + 1];
                        if (keyEl.Name.LocalName != "key") continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(System.Text.Json.JsonSerializer.Serialize(keyEl.Value));
                        sb.Append(':');
                        WritePlistNode(valEl, sb);
                    }
                    sb.Append('}');
                    break;

                case "array":
                    sb.Append('[');
                    var arrFirst = true;
                    foreach (var child in element.Elements())
                    {
                        if (!arrFirst) sb.Append(',');
                        arrFirst = false;
                        WritePlistNode(child, sb);
                    }
                    sb.Append(']');
                    break;

                case "string":
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(element.Value));
                    break;

                case "integer":
                    sb.Append(long.TryParse(element.Value, out var lval) ? lval.ToString() : "0");
                    break;

                case "real":
                    sb.Append(double.TryParse(element.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var dval)
                        ? dval.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0");
                    break;

                case "true":
                    sb.Append("true");
                    break;

                case "false":
                    sb.Append("false");
                    break;

                default:
                    sb.Append("null");
                    break;
            }
        }

        private static bool IsTmGrammarFile(string path) =>
            path.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tmGrammar", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tmGrammar.json", StringComparison.OrdinalIgnoreCase);

        public ICollection<string> GetInjections(string scopeName) => null!;

        public IRawTheme GetTheme(string scopeName) => EmptyRawTheme.Instance;

        public IRawTheme GetDefaultTheme() => EmptyRawTheme.Instance;

        /// <summary>
        /// Minimal IRawTheme that satisfies TextMateSharp's Registry constructor.
        /// We do our own color resolution so we don't need theme-based coloring from TextMateSharp.
        /// </summary>
        private sealed class EmptyRawTheme : IRawTheme
        {
            public static readonly EmptyRawTheme Instance = new();
            public string GetName() => string.Empty;
            public ICollection<IRawThemeSetting> GetSettings() => [];
            public ICollection<IRawThemeSetting> GetTokenColors() => [];
            public ICollection<KeyValuePair<string, object>> GetGuiColors() => [];
            public string? GetInclude() => null;
        }

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

    private sealed record CandidateGrammar(string ScopeName, IGrammar? Grammar);
}
