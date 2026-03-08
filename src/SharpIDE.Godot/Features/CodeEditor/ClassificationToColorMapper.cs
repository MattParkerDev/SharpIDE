using Godot;

namespace SharpIDE.Godot.Features.CodeEditor;

public static class ClassificationToColorMapper
{
    public static Color GetColorForClassification(EditorThemeColorSet editorThemeColorSet, string classificationType)
    {
        var colour = classificationType switch
        {
            // Keywords
            "keyword" => editorThemeColorSet.KeywordBlue,
            "keyword - control" => editorThemeColorSet.KeywordBlue,
            "preprocessor keyword" => editorThemeColorSet.KeywordBlue,

            // Literals & comments
            "string" => editorThemeColorSet.LightOrangeBrown,
            "string - verbatim" => editorThemeColorSet.LightOrangeBrown,
            "string - escape character" => editorThemeColorSet.Orange,
            "comment" => editorThemeColorSet.CommentGreen,
            "number" => editorThemeColorSet.NumberGreen,

            // Types (User Types)
            "class name" => editorThemeColorSet.ClassGreen,
            "record class name" => editorThemeColorSet.ClassGreen,
            "struct name" => editorThemeColorSet.ClassGreen,
            "record struct name" => editorThemeColorSet.ClassGreen,
            "interface name" => editorThemeColorSet.InterfaceGreen,
            "enum name" => editorThemeColorSet.InterfaceGreen,
            "namespace name" => editorThemeColorSet.White,
            
            // Identifiers & members
            "identifier" => editorThemeColorSet.White,
            "constant name" => editorThemeColorSet.White,
            "enum member name" => editorThemeColorSet.White,
            "method name" => editorThemeColorSet.Yellow,
            "extension method name" => editorThemeColorSet.Yellow,
            "property name" => editorThemeColorSet.White,
            "field name" => editorThemeColorSet.White,
            "static symbol" => editorThemeColorSet.Yellow, // ??
            "parameter name" => editorThemeColorSet.VariableBlue,
            "local name" => editorThemeColorSet.VariableBlue,
            "type parameter name" => editorThemeColorSet.ClassGreen,
            "delegate name" => editorThemeColorSet.ClassGreen,
            "event name" => editorThemeColorSet.White,
            "label name" => editorThemeColorSet.White,

            // Punctuation & operators
            "operator" => editorThemeColorSet.White,
            "operator - overloaded" => editorThemeColorSet.Yellow,
            "punctuation" => editorThemeColorSet.White,
            
            // Preprocessor
            "preprocessor text" => editorThemeColorSet.White,
            
            // Xml comments
            "xml doc comment - delimiter" => editorThemeColorSet.CommentGreen,
            "xml doc comment - name" => editorThemeColorSet.White,
            "xml doc comment - text" => editorThemeColorSet.CommentGreen,
            "xml doc comment - attribute name" => editorThemeColorSet.Gray,
            "xml doc comment - attribute quotes" => editorThemeColorSet.LightOrangeBrown,
            "xml doc comment - attribute value" => editorThemeColorSet.LightOrangeBrown,

            // Misc
            "excluded code" => editorThemeColorSet.Gray,
            "text" => editorThemeColorSet.White,
            "whitespace" => editorThemeColorSet.White,

            _ => editorThemeColorSet.Pink // pink, warning color for unhandled classifications
        };
        if (colour == editorThemeColorSet.Pink)
        {
            GD.PrintErr($"Unhandled classification type: '{classificationType}'");
        }
        return colour;
    }
}