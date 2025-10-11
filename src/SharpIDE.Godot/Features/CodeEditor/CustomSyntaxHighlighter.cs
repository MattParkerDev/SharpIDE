using Godot;
using Godot.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using SharpIDE.RazorAccess;

namespace SharpIDE.Godot;

public partial class CustomHighlighter : SyntaxHighlighter
{
    private readonly Dictionary _emptyDict = new();
    public HashSet<(FileLinePositionSpan fileSpan, ClassifiedSpan classifiedSpan)> ClassifiedSpans = [];
    public HashSet<SharpIdeRazorClassifiedSpan> RazorClassifiedSpans = [];
    public override Dictionary _GetLineSyntaxHighlighting(int line)
    {
        var highlights = (ClassifiedSpans, RazorClassifiedSpans) switch
        {
            ({ Count: 0 }, { Count: 0 }) => _emptyDict,
            ({ Count: > 0 }, _) => MapClassifiedSpansToHighlights(line),
            (_, { Count: > 0 }) => MapRazorClassifiedSpansToHighlights(line),
            _ => throw new NotImplementedException("Both ClassifiedSpans and RazorClassifiedSpans are set. This is not supported yet.")
        };

        return highlights;
    }
    
    private static readonly StringName ColorStringName = "color";
    private Dictionary MapRazorClassifiedSpansToHighlights(int line)
    {
        var highlights = new Dictionary();

        // Filter spans on the given line, ignore empty spans
        var spansForLine = RazorClassifiedSpans
            .Where(s => s.Span.LineIndex == line && s.Span.Length is not 0)
            .GroupBy(s => s.Span)
            .ToList();

        foreach (var razorSpanGrouping in spansForLine)
        {
            var spans = razorSpanGrouping.ToList();
            if (spans.Count > 2) throw new NotImplementedException("More than 2 classified spans is not supported yet.");
            if (spans.Count is not 1)
            {
                if (spans.Any(s => s.Kind is SharpIdeRazorSpanKind.Code))
                {
                    spans = spans.Where(s => s.Kind is SharpIdeRazorSpanKind.Code).ToList();
                }
                if (spans.Count is not 1)
                {
                    SharpIdeRazorClassifiedSpan? staticClassifiedSpan = spans.FirstOrDefault(s => s.CodeClassificationType == ClassificationTypeNames.StaticSymbol);
                    if (staticClassifiedSpan is not null) spans.Remove(staticClassifiedSpan.Value);
                }
            }
            var razorSpan = spans.Single();
            
            int columnIndex = razorSpan.Span.CharacterIndex;
            
            var highlightInfo = new Dictionary
            {
                { ColorStringName, GetColorForRazorSpanKind(razorSpan.Kind, razorSpan.CodeClassificationType, razorSpan.VsSemanticRangeType) }
            };

            highlights[columnIndex] = highlightInfo;
        }

        return highlights;
    }
    
    private static Color GetColorForRazorSpanKind(SharpIdeRazorSpanKind kind, string? codeClassificationType, string? vsSemanticRangeType)
    {
        return kind switch
        {
            SharpIdeRazorSpanKind.Code => GetColorForClassification(codeClassificationType!),
            SharpIdeRazorSpanKind.Comment => CachedColors.CommentGreen, // green
            SharpIdeRazorSpanKind.MetaCode => CachedColors.RazorMetaCodePurple, // purple
            SharpIdeRazorSpanKind.Markup => GetColorForMarkupSpanKind(vsSemanticRangeType),
            SharpIdeRazorSpanKind.Transition => CachedColors.RazorMetaCodePurple, // purple
            SharpIdeRazorSpanKind.None => CachedColors.White,
            _ => CachedColors.White
        };
    }
    
    private static Color GetColorForMarkupSpanKind(string? vsSemanticRangeType)
    {
        return vsSemanticRangeType switch
        {
            "razorDirective" or "razorTransition" => CachedColors.RazorMetaCodePurple, // purple
            "markupTagDelimiter" => CachedColors.HtmlDelimiterGray, // gray
            "markupTextLiteral" => CachedColors.White, // white
            "markupElement" => CachedColors.KeywordBlue, // blue
            "razorComponentElement" => CachedColors.RazorComponentGreen, // dark green
            "razorComponentAttribute" => CachedColors.White, // white
            "razorComment" or "razorCommentStar" or "razorCommentTransition" => CachedColors.CommentGreen, // green
            "markupOperator" => CachedColors.White, // white
            "markupAttributeQuote" => CachedColors.White, // white
            _ => CachedColors.White // default to white
        };
    }

    
    private Dictionary MapClassifiedSpansToHighlights(int line)
    {
        var highlights = new Dictionary();
        
        // consider no linq or ZLinq
        var spansGroupedByFileSpan = ClassifiedSpans
            .Where(s => s.fileSpan.StartLinePosition.Line == line && s.classifiedSpan.TextSpan.Length is not 0)
            .GroupBy(span => span.fileSpan)
            .Select(group => (fileSpan: group.Key, classifiedSpans: group.Select(s => s.classifiedSpan).ToList()));

        foreach (var (fileSpan, classifiedSpans) in spansGroupedByFileSpan)
        {
            if (classifiedSpans.Count > 2) throw new NotImplementedException("More than 2 classified spans is not supported yet.");
            if (classifiedSpans.Count is not 1)
            {
                ClassifiedSpan? staticClassifiedSpan = classifiedSpans.FirstOrDefault(s => s.ClassificationType == ClassificationTypeNames.StaticSymbol);
                if (staticClassifiedSpan is not null) classifiedSpans.Remove(staticClassifiedSpan.Value);
            }
            // Column index of the first character in this span
            int columnIndex = fileSpan.StartLinePosition.Character;

            // Build the highlight entry
            var highlightInfo = new Dictionary
            {
                { ColorStringName, GetColorForClassification(classifiedSpans.Single().ClassificationType) }
            };

            highlights[columnIndex] = highlightInfo;
        }

        return highlights;
    }
    
    private static Color GetColorForClassification(string classificationType)
    {
        var colour = classificationType switch
        {
            // Keywords
            "keyword" => CachedColors.KeywordBlue,
            "keyword - control" => CachedColors.KeywordBlue,
            "preprocessor keyword" => CachedColors.KeywordBlue,

            // Literals & comments
            "string" => CachedColors.LightOrangeBrown,
            "comment" => CachedColors.CommentGreen,
            "number" => CachedColors.NumberGreen,

            // Types (User Types)
            "class name" => CachedColors.ClassGreen,
            "record class name" => CachedColors.ClassGreen,
            "struct name" => CachedColors.ClassGreen,
            "interface name" => CachedColors.InterfaceGreen,
            "enum name" => CachedColors.InterfaceGreen,
            "namespace name" => CachedColors.White,
            
            // Identifiers & members
            "identifier" => CachedColors.White,
            "constant name" => CachedColors.White,
            "enum member name" => CachedColors.White,
            "method name" => CachedColors.Yellow,
            "extension method name" => CachedColors.Yellow,
            "property name" => CachedColors.White,
            "field name" => CachedColors.White,
            "static symbol" => CachedColors.Yellow, // ??
            "parameter name" => CachedColors.VariableBlue,
            "local name" => CachedColors.VariableBlue,

            // Punctuation & operators
            "operator" => CachedColors.White,
            "operator - overloaded" => CachedColors.Yellow,
            "punctuation" => CachedColors.White,
            
            // Xml comments
            "xml doc comment - delimiter" => CachedColors.CommentGreen,
            "xml doc comment - name" => CachedColors.White,
            "xml doc comment - text" => CachedColors.CommentGreen,

            // Misc
            "excluded code" => CachedColors.Gray,

            _ => CachedColors.Orange // orange, warning color for unhandled classifications
        };
        if (colour == CachedColors.Orange)
        {
            GD.PrintErr($"Unhandled classification type: '{classificationType}'");
        }
        return colour;
    }
}

public static class CachedColors
{
    public static readonly Color Orange = new("f27718");
    public static readonly Color White = new("dcdcdc");
    public static readonly Color Yellow = new("dcdcaa");
    public static readonly Color CommentGreen = new("57a64a");
    public static readonly Color KeywordBlue = new("569cd6");
    public static readonly Color LightOrangeBrown = new("d69d85");
    public static readonly Color NumberGreen = new("b5cea8");
    public static readonly Color InterfaceGreen = new("b8d7a3");
    public static readonly Color ClassGreen = new("4ec9b0");
    public static readonly Color VariableBlue = new("9cdcfe");
    public static readonly Color Gray = new("a9a9a9");
    
    public static readonly Color RazorComponentGreen = new("0b7f7f");
    public static readonly Color RazorMetaCodePurple = new("a699e6");
    public static readonly Color HtmlDelimiterGray = new("808080");
}