using System.Collections.Immutable;
using Godot;
using Godot.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Godot.Features.CodeEditor;

namespace SharpIDE.Godot;

public partial class CustomHighlighter : SyntaxHighlighter
{
    private readonly Dictionary _emptyDict = new();

    private System.Collections.Generic.Dictionary<int, ImmutableArray<SharpIdeRazorClassifiedSpan>> _razorClassifiedSpansByLine = [];
    private System.Collections.Generic.Dictionary<int, ImmutableArray<SharpIdeClassifiedSpan>> _classifiedSpansByLine = [];
    
    
    public void SetHighlightingData(ImmutableArray<SharpIdeClassifiedSpan> classifiedSpans, ImmutableArray<SharpIdeRazorClassifiedSpan> razorClassifiedSpans)
    {
        // separate each line here
        var razorSpansForLine = razorClassifiedSpans
            .Where(s => s.Span.Length is not 0)
            .GroupBy(s => s.Span.LineIndex);
        
        _razorClassifiedSpansByLine = razorSpansForLine.ToDictionary(g => g.Key, g => g.ToImmutableArray());

        var spansGroupedByFileSpan = classifiedSpans
            .Where(s => s.ClassifiedSpan.TextSpan.Length is not 0)
            .GroupBy(span => span.FileSpan.Start.Line);
        
        _classifiedSpansByLine = spansGroupedByFileSpan.ToDictionary(g => g.Key, g => g.ToImmutableArray());
    }

    // Indicates that lines were removed or added, and the overall result of that is that a line (wasLineNumber), is now (becameLineNumber)
    // So if you added a line above line 10, then wasLineNumber=10, becameLineNumber=11
    // If you removed a line above line 10, then wasLineNumber=10, becameLineNumber=9
    //
    // This is all a very dodgy workaround to move highlighting up and down, while we wait for the workspace to return us highlighting for the updated file
    public void LinesChanged(long wasLineNumber, long becameLineNumber, SharpIdeCodeEdit.LineEditOrigin origin)
    {
        var difference = (int)(becameLineNumber - wasLineNumber);
        if (difference is 0) return;
        if (difference > 0)
        {
            LinesAdded(wasLineNumber, difference, origin);
        }
        else
        {
            LinesRemoved(wasLineNumber, -difference);
        }
    }

    private void LinesAdded(long fromLine, int difference, SharpIdeCodeEdit.LineEditOrigin origin)
    {
        _razorClassifiedSpansByLine = Rearrange(_razorClassifiedSpansByLine, fromLine, difference, origin);
        _classifiedSpansByLine = Rearrange(_classifiedSpansByLine, fromLine, difference, origin);
        return;

        static System.Collections.Generic.Dictionary<int, T> Rearrange<T>(System.Collections.Generic.Dictionary<int, T> existingDictionary, long fromLine, int difference, SharpIdeCodeEdit.LineEditOrigin origin)
        {
            var newDict = new System.Collections.Generic.Dictionary<int, T>();
            foreach (var kvp in existingDictionary)
            {
                bool shouldShift =
                    kvp.Key > fromLine ||                // always shift lines after the insertion point
                    (origin == SharpIdeCodeEdit.LineEditOrigin.StartOfLine && kvp.Key == fromLine); // shift current line if origin is Start

                int newKey = shouldShift ? kvp.Key + difference : kvp.Key;
                newDict[newKey] = kvp.Value;
            }
            return newDict;
        }
    }
    
    private void LinesRemoved(long fromLine, int numberOfLinesRemoved)
    {
        _classifiedSpansByLine = Rearrange(_classifiedSpansByLine, fromLine, numberOfLinesRemoved);
        _razorClassifiedSpansByLine = Rearrange(_razorClassifiedSpansByLine, fromLine, numberOfLinesRemoved);
        return;

        static System.Collections.Generic.Dictionary<int, T> Rearrange<T>(System.Collections.Generic.Dictionary<int, T> existingDictionary, long fromLine, int numberOfLinesRemoved)
        {
            // everything from 'fromLine' onwards needs to be shifted up by numberOfLinesRemoved
            var newDict = new System.Collections.Generic.Dictionary<int, T>();
            foreach (var kvp in existingDictionary)
            {
                if (kvp.Key < fromLine)
                {
                    newDict[kvp.Key] = kvp.Value;
                }
                else if (kvp.Key == fromLine)
                {
                    newDict[kvp.Key - numberOfLinesRemoved] = kvp.Value;
                }
                else if (kvp.Key >= fromLine + numberOfLinesRemoved)
                {
                    newDict[kvp.Key - numberOfLinesRemoved] = kvp.Value;
                } 
            }
            return newDict;
        }
    }
    
    public override Dictionary _GetLineSyntaxHighlighting(int line)
    {
        var highlights = (_classifiedSpansByLine, _razorClassifiedSpansByLine) switch
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
        if (_razorClassifiedSpansByLine.TryGetValue(line, out var razorSpansForLine) is false) return highlights;
        
        // group by span (start, length matches)
        var spansGroupedByFileSpan = razorSpansForLine.GroupBy(span => span.Span);

        foreach (var razorSpanGrouping in spansGroupedByFileSpan)
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
        if (_classifiedSpansByLine.TryGetValue(line, out var spansForLine) is false) return highlights;
        
        // consider no linq or ZLinq
        // group by span (start, length matches)
        var spansGroupedByFileSpan = spansForLine
            .GroupBy(span => span.FileSpan)
            .Select(group => (fileSpan: group.Key, classifiedSpans: group.Select(s => s.ClassifiedSpan).ToList()));

        foreach (var (fileSpan, classifiedSpans) in spansGroupedByFileSpan)
        {
            if (classifiedSpans.Count > 2) throw new NotImplementedException("More than 2 classified spans is not supported yet.");
            if (classifiedSpans.Count is not 1)
            {
                ClassifiedSpan? staticClassifiedSpan = classifiedSpans.FirstOrDefault(s => s.ClassificationType == ClassificationTypeNames.StaticSymbol);
                if (staticClassifiedSpan is not null) classifiedSpans.Remove(staticClassifiedSpan.Value);
            }
            // Column index of the first character in this span
            int columnIndex = fileSpan.Start.Character;

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
            "string - verbatim" => CachedColors.LightOrangeBrown,
            "string - escape character" => CachedColors.Orange,
            "comment" => CachedColors.CommentGreen,
            "number" => CachedColors.NumberGreen,

            // Types (User Types)
            "class name" => CachedColors.ClassGreen,
            "record class name" => CachedColors.ClassGreen,
            "struct name" => CachedColors.ClassGreen,
            "record struct name" => CachedColors.ClassGreen,
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
            "type parameter name" => CachedColors.ClassGreen,
            "delegate name" => CachedColors.ClassGreen,
            "event name" => CachedColors.White,
            "label name" => CachedColors.White,

            // Punctuation & operators
            "operator" => CachedColors.White,
            "operator - overloaded" => CachedColors.Yellow,
            "punctuation" => CachedColors.White,
            
            // Preprocessor
            "preprocessor text" => CachedColors.White,
            
            // Xml comments
            "xml doc comment - delimiter" => CachedColors.CommentGreen,
            "xml doc comment - name" => CachedColors.White,
            "xml doc comment - text" => CachedColors.CommentGreen,
            "xml doc comment - attribute name" => CachedColors.LightOrangeBrown,
            "xml doc comment - attribute quotes" => CachedColors.LightOrangeBrown,

            // Misc
            "excluded code" => CachedColors.Gray,

            _ => CachedColors.Pink // pink, warning color for unhandled classifications
        };
        if (colour == CachedColors.Pink)
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
    public static readonly Color Pink = new("c586c0");
    public static readonly Color ErrorRed = new("da5b5a");
    
    public static readonly Color RazorComponentGreen = new("0b7f7f");
    public static readonly Color RazorMetaCodePurple = new("a699e6");
    public static readonly Color HtmlDelimiterGray = new("808080");
}
