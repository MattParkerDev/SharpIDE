using System.Runtime.CompilerServices;
using Godot;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static class ClassificationToColorMapper
{
    private static readonly ConditionalWeakTable<EditorThemeColorSet, Color[]> _colorTables = new();

    /// <summary>Colours indexed by <see cref="SharpIdeClassification"/>, cached per theme colour set.</summary>
    public static Color[] GetColorTable(EditorThemeColorSet editorThemeColorSet)
        => _colorTables.GetValue(editorThemeColorSet, BuildColorTable);

    public static Color GetColorForClassification(EditorThemeColorSet editorThemeColorSet, SharpIdeClassification classification)
        => GetColorTable(editorThemeColorSet)[(int)classification];

    public static Color GetColorForClassification(EditorThemeColorSet editorThemeColorSet, string classificationType)
    {
        var classification = SharpIdeClassifications.FromName(classificationType);
        if (classification is SharpIdeClassification.Unknown)
        {
            GD.PrintErr($"Unhandled classification type: '{classificationType}'");
        }
        return GetColorForClassification(editorThemeColorSet, classification);
    }

    private static Color[] BuildColorTable(EditorThemeColorSet colorSet)
    {
        var table = new Color[SharpIdeClassifications.Count];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = GetColor(colorSet, (SharpIdeClassification)i);
        }
        return table;
    }

    private static Color GetColor(EditorThemeColorSet c, SharpIdeClassification classification) => classification switch
    {
        SharpIdeClassification.Text or SharpIdeClassification.WhiteSpace => c.White,

        // Keywords
        SharpIdeClassification.Keyword or SharpIdeClassification.ControlKeyword or SharpIdeClassification.PreprocessorKeyword => c.KeywordBlue,

        // Literals & comments
        SharpIdeClassification.StringLiteral or SharpIdeClassification.VerbatimStringLiteral => c.LightOrangeBrown,
        SharpIdeClassification.StringEscapeCharacter => c.Orange,
        SharpIdeClassification.Comment => c.CommentGreen,
        SharpIdeClassification.NumericLiteral => c.NumberGreen,

        // Types
        SharpIdeClassification.ClassName or SharpIdeClassification.RecordClassName or SharpIdeClassification.StructName
            or SharpIdeClassification.RecordStructName or SharpIdeClassification.ModuleName
            or SharpIdeClassification.TypeParameterName or SharpIdeClassification.DelegateName => c.ClassGreen,
        SharpIdeClassification.InterfaceName or SharpIdeClassification.EnumName => c.InterfaceGreen,
        SharpIdeClassification.NamespaceName => c.White,

        // Identifiers & members
        SharpIdeClassification.Identifier or SharpIdeClassification.ConstantName or SharpIdeClassification.EnumMemberName
            or SharpIdeClassification.PropertyName or SharpIdeClassification.FieldName
            or SharpIdeClassification.EventName or SharpIdeClassification.LabelName => c.White,
        SharpIdeClassification.MethodName or SharpIdeClassification.ExtensionMethodName => c.Yellow,
        SharpIdeClassification.ParameterName or SharpIdeClassification.LocalName => c.VariableBlue,

        // Punctuation & operators
        SharpIdeClassification.Operator or SharpIdeClassification.Punctuation => c.White,
        SharpIdeClassification.OperatorOverloaded => c.Yellow,

        // Preprocessor
        SharpIdeClassification.PreprocessorText => c.White,
        SharpIdeClassification.ExcludedCode => c.Gray,

        // Xml doc comments
        SharpIdeClassification.XmlDocCommentDelimiter or SharpIdeClassification.XmlDocCommentText
            or SharpIdeClassification.XmlDocCommentComment or SharpIdeClassification.XmlDocCommentCDataSection
            or SharpIdeClassification.XmlDocCommentEntityReference or SharpIdeClassification.XmlDocCommentProcessingInstruction => c.CommentGreen,
        SharpIdeClassification.XmlDocCommentName => c.White,
        SharpIdeClassification.XmlDocCommentAttributeName => c.Gray,
        SharpIdeClassification.XmlDocCommentAttributeQuotes or SharpIdeClassification.XmlDocCommentAttributeValue => c.LightOrangeBrown,

        // Regex (embedded in strings)
        SharpIdeClassification.RegexComment => c.CommentGreen,
        SharpIdeClassification.RegexText or SharpIdeClassification.RegexSelfEscapedCharacter => c.LightOrangeBrown,
        SharpIdeClassification.RegexCharacterClass or SharpIdeClassification.RegexAnchor or SharpIdeClassification.RegexQuantifier
            or SharpIdeClassification.RegexGrouping or SharpIdeClassification.RegexAlternation or SharpIdeClassification.RegexOtherEscape => c.Orange,

        // Json (embedded in strings)
        SharpIdeClassification.JsonComment => c.CommentGreen,
        SharpIdeClassification.JsonNumber => c.NumberGreen,
        SharpIdeClassification.JsonString => c.LightOrangeBrown,
        SharpIdeClassification.JsonKeyword => c.KeywordBlue,
        SharpIdeClassification.JsonConstructorName => c.ClassGreen,
        SharpIdeClassification.JsonText or SharpIdeClassification.JsonOperator or SharpIdeClassification.JsonPunctuation
            or SharpIdeClassification.JsonArray or SharpIdeClassification.JsonObject or SharpIdeClassification.JsonPropertyName => c.White,

        // Razor / markup
        SharpIdeClassification.RazorDirective or SharpIdeClassification.RazorTransition
            or SharpIdeClassification.RazorDirectiveAttribute or SharpIdeClassification.RazorDirectiveColon => c.RazorMetaCodePurple,
        SharpIdeClassification.MarkupTagDelimiter => c.HtmlDelimiterGray,
        SharpIdeClassification.MarkupElement => c.KeywordBlue,
        SharpIdeClassification.RazorComponentElement or SharpIdeClassification.RazorTagHelperElement => c.RazorComponentGreen,
        SharpIdeClassification.RazorComment or SharpIdeClassification.RazorCommentStar or SharpIdeClassification.RazorCommentTransition
            or SharpIdeClassification.MarkupComment or SharpIdeClassification.MarkupCommentPunctuation => c.CommentGreen,
        SharpIdeClassification.MarkupTextLiteral or SharpIdeClassification.MarkupAttribute or SharpIdeClassification.MarkupAttributeValue
            or SharpIdeClassification.MarkupAttributeQuote or SharpIdeClassification.MarkupOperator
            or SharpIdeClassification.RazorComponentAttribute or SharpIdeClassification.RazorTagHelperAttribute => c.White,

        SharpIdeClassification.Unknown => c.Pink, // warning colour for unhandled classifications
        _ => c.Pink
    };
}
