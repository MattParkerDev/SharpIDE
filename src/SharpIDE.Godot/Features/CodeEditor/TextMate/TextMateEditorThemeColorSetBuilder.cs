using Godot;

namespace SharpIDE.Godot.Features.CodeEditor.TextMate;

/// <summary>
/// Builds an EditorThemeColorSet from a parsed TextMateTheme.
/// Maps each named color slot (e.g., KeywordBlue) to appropriate TextMate scopes,
/// resolves colors from the theme, and falls back to a default palette for unmatched scopes.
/// </summary>
public static class TextMateEditorThemeColorSetBuilder
{
    /// <summary>
    /// Maps EditorThemeColorSet named slots to their canonical TextMate scope chains.
    /// Each entry is ordered from most-specific to least-specific scope.
    /// </summary>
    private static readonly Dictionary<string, string[]> ColorSlotToScopes = new()
    {
        ["Orange"] = ["constant.character.escape", "constant.character"],
        ["White"] = ["text"],
        ["Yellow"] = ["entity.name.function"],
        ["CommentGreen"] = ["comment.line", "comment"],
        ["KeywordBlue"] = ["keyword.control", "keyword"],
        ["LightOrangeBrown"] = ["string.quoted.double", "string"],
        ["NumberGreen"] = ["constant.numeric"],
        ["InterfaceGreen"] = ["entity.name.type.interface", "entity.name.type"],
        ["ClassGreen"] = ["entity.name.type.class", "entity.name.type"],
        ["VariableBlue"] = ["variable.parameter", "variable"],
        ["Gray"] = ["comment.block"],
        ["Pink"] = ["entity.name.type"],
        ["ErrorRed"] = ["invalid"],
        ["RazorComponentGreen"] = ["entity.name.type"],
        ["RazorMetaCodePurple"] = ["keyword.preprocessor", "keyword"],
        ["HtmlDelimiterGray"] = ["punctuation.definition.tag"],
    };

    /// <summary>
    /// Builds an EditorThemeColorSet from a TextMateTheme.
    /// Falls back to a default palette (e.g., EditorThemeColours.Dark) for any unmatched scopes.
    /// </summary>
    public static EditorThemeColorSet Build(TextMateTheme theme, EditorThemeColorSet fallback)
    {
        return new EditorThemeColorSet
        {
            Orange = ResolveColor(theme, "Orange", fallback.Orange),
            White = ResolveColor(theme, "White", fallback.White),
            Yellow = ResolveColor(theme, "Yellow", fallback.Yellow),
            CommentGreen = ResolveColor(theme, "CommentGreen", fallback.CommentGreen),
            KeywordBlue = ResolveColor(theme, "KeywordBlue", fallback.KeywordBlue),
            LightOrangeBrown = ResolveColor(theme, "LightOrangeBrown", fallback.LightOrangeBrown),
            NumberGreen = ResolveColor(theme, "NumberGreen", fallback.NumberGreen),
            InterfaceGreen = ResolveColor(theme, "InterfaceGreen", fallback.InterfaceGreen),
            ClassGreen = ResolveColor(theme, "ClassGreen", fallback.ClassGreen),
            VariableBlue = ResolveColor(theme, "VariableBlue", fallback.VariableBlue),
            Gray = ResolveColor(theme, "Gray", fallback.Gray),
            Pink = ResolveColor(theme, "Pink", fallback.Pink),
            ErrorRed = ResolveColor(theme, "ErrorRed", fallback.ErrorRed),
            RazorComponentGreen = ResolveColor(theme, "RazorComponentGreen", fallback.RazorComponentGreen),
            RazorMetaCodePurple = ResolveColor(theme, "RazorMetaCodePurple", fallback.RazorMetaCodePurple),
            HtmlDelimiterGray = ResolveColor(theme, "HtmlDelimiterGray", fallback.HtmlDelimiterGray),
        };
    }

    private static Color ResolveColor(TextMateTheme theme, string colorSlotName, Color fallback)
    {
        if (!ColorSlotToScopes.TryGetValue(colorSlotName, out var scopes))
        {
            return fallback;
        }

        var color = theme.ResolveColor(scopes);
        return color ?? fallback;
    }
}
