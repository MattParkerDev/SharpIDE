namespace SharpIDE.Godot.Features.CodeEditor.TextMate;

/// <summary>
/// Static helper to map Roslyn classification type names to TextMate scope chains.
/// Each Roslyn classification maps to a list of scope names (most to least specific).
/// The TextMate theme will use longest-prefix-match to resolve colors.
/// </summary>
public static class RoslynToTextMateScopes
{
    private static readonly Dictionary<string, string[]> RoslynToScopesMap = new()
    {
        // Keywords
        ["keyword"] = ["keyword.other", "keyword"],
        ["keyword - control"] = ["keyword.control", "keyword"],
        ["preprocessor keyword"] = ["keyword.preprocessor", "keyword.other", "keyword"],

        // Literals
        ["string"] = ["string.quoted.double", "string"],
        ["string - verbatim"] = ["string.quoted.other", "string"],
        ["string - escape character"] = ["constant.character.escape", "constant.character", "constant"],
        ["number"] = ["constant.numeric", "constant"],

        // Comments
        ["comment"] = ["comment.line", "comment"],

        // Type definitions
        ["class name"] = ["entity.name.type.class", "entity.name.type", "entity.name"],
        ["record class name"] = ["entity.name.type.class", "entity.name.type", "entity.name"],
        ["struct name"] = ["entity.name.type.struct", "entity.name.type", "entity.name"],
        ["record struct name"] = ["entity.name.type.struct", "entity.name.type", "entity.name"],
        ["interface name"] = ["entity.name.type.interface", "entity.name.type", "entity.name"],
        ["enum name"] = ["entity.name.type.enum", "entity.name.type", "entity.name"],
        ["namespace name"] = ["entity.name.namespace", "entity.name"],
        ["delegate name"] = ["entity.name.type", "entity.name"],
        ["type parameter name"] = ["entity.name.type.parameter", "entity.name.type", "entity.name"],

        // Identifiers
        ["identifier"] = ["variable.other", "variable"],
        ["constant name"] = ["variable.other.constant", "constant", "variable"],
        ["enum member name"] = ["variable.other.enummember", "variable.other", "variable"],

        // Members and attributes
        ["method name"] = ["entity.name.function", "entity.name"],
        ["extension method name"] = ["entity.name.function", "entity.name"],
        ["property name"] = ["variable.other.property", "variable.other", "variable"],
        ["field name"] = ["variable.other.property", "variable.other", "variable"],
        ["event name"] = ["entity.name.other", "entity.name"],
        ["label name"] = ["entity.name.label", "entity.name"],

        // Parameters and variables
        ["parameter name"] = ["variable.parameter", "variable"],
        ["local name"] = ["variable.other.readwrite", "variable.other", "variable"],
        ["static symbol"] = ["storage.modifier.static", "storage.modifier", "storage"],

        // Operators and punctuation
        ["operator"] = ["keyword.operator", "keyword"],
        ["operator - overloaded"] = ["keyword.operator.overloaded", "keyword.operator", "keyword"],
        ["punctuation"] = ["punctuation"],

        // Preprocessor
        ["preprocessor text"] = ["meta.preprocessor", "comment"],

        // XML documentation comments
        ["xml doc comment - delimiter"] = ["comment.block.documentation", "comment"],
        ["xml doc comment - name"] = ["variable.other", "variable"],
        ["xml doc comment - text"] = ["comment.block.documentation", "comment"],
        ["xml doc comment - attribute name"] = ["entity.other.attribute-name", "entity.other"],
        ["xml doc comment - attribute quotes"] = ["punctuation.definition.string", "punctuation"],
        ["xml doc comment - attribute value"] = ["string.quoted.single", "string"],

        // Misc
        ["excluded code"] = ["comment.block", "comment"],
        ["text"] = ["text"],
        ["whitespace"] = ["text"],
    };

    /// <summary>
    /// Gets the TextMate scope chain for a Roslyn classification type.
    /// Returns an ordered array from most-specific to least-specific scope.
    /// </summary>
    public static string[] GetScopes(string roslynClassificationType)
    {
        if (RoslynToScopesMap.TryGetValue(roslynClassificationType, out var scopes))
        {
            return scopes;
        }

        // Fallback: unknown classifications map to generic "variable" scope
        return ["variable.other", "variable", "text"];
    }
}
