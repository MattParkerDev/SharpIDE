using Godot;

namespace SharpIDE.Godot.Features.CodeEditor.TextMate;

/// <summary>
/// Represents a parsed TextMate theme with token color rules.
/// Supports scope-based color resolution using TextMate's longest-prefix-match algorithm.
/// </summary>
public class TextMateTheme
{
    public string Name { get; init; } = "Unknown";
    public string ThemeType { get; init; } = "dark"; // "dark" or "light"
    public List<TextMateTokenRule> TokenRules { get; init; } = [];

    /// <summary>
    /// Resolves a color for the given scope chain using longest-prefix-match.
    /// The scope chain should be ordered from most-specific to least-specific.
    /// Example: ["keyword.control.cs", "keyword.control", "keyword", "source.cs"]
    ///
    /// Returns the foreground color of the first matching rule, or null if no match found.
    /// </summary>
    public Color? ResolveColor(IEnumerable<string> scopeChain)
    {
        foreach (var scope in scopeChain)
        {
            var longestMatch = FindLongestMatchingRule(scope);
            if (longestMatch?.Foreground != null)
            {
                return longestMatch.Foreground;
            }
        }
        return null;
    }

    private TextMateTokenRule? FindLongestMatchingRule(string scope)
    {
        TextMateTokenRule? longestMatchRule = null;
        int longestMatchLength = 0;

        foreach (var rule in TokenRules)
        {
            foreach (var ruleScope in rule.Scopes)
            {
                // Check if ruleScope is a prefix of scope (TextMate matching)
                if (scope.StartsWith(ruleScope) || scope == ruleScope)
                {
                    // It's a match; update if it's longer than previous best match
                    if (ruleScope.Length > longestMatchLength)
                    {
                        longestMatchLength = ruleScope.Length;
                        longestMatchRule = rule;
                    }
                }
            }
        }

        return longestMatchRule;
    }
}

/// <summary>
/// A single token color rule in a TextMate theme.
/// Associates one or more scope names with a foreground color (and optionally background/font style).
/// </summary>
public class TextMateTokenRule
{
    public string[] Scopes { get; init; } = [];
    public Color? Foreground { get; init; }
    public Color? Background { get; init; }
    public string? FontStyle { get; init; }
}
