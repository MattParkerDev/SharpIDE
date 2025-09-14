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
                { ColorStringName, GetColorForRazorSpanKind(razorSpan.Kind, razorSpan.CodeClassificationType) }
            };

            highlights[columnIndex] = highlightInfo;
        }

        return highlights;
    }
    
    private static Color GetColorForRazorSpanKind(SharpIdeRazorSpanKind kind, string? codeClassificationType)
    {
        return kind switch
        {
            SharpIdeRazorSpanKind.Code => GetColorForClassification(codeClassificationType!),
            SharpIdeRazorSpanKind.Comment => new Color("57a64a"), // green
            SharpIdeRazorSpanKind.MetaCode => new Color("a699e6"), // purple
            SharpIdeRazorSpanKind.Markup => new Color("0b7f7f"), // dark green
            SharpIdeRazorSpanKind.Transition => new Color("a699e6"), // purple
            SharpIdeRazorSpanKind.None => new Color("dcdcdc"),
            _ => new Color("dcdcdc")
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
        return classificationType switch
        {
            // Keywords
            "keyword" => new Color("569cd6"),
            "keyword - control" => new Color("569cd6"),
            "preprocessor keyword" => new Color("569cd6"),

            // Literals & comments
            "string" => new Color("d69d85"),
            "comment" => new Color("57a64a"),
            "number" => new Color("b5cea8"),

            // Types (User Types)
            "class name" => new Color("4ec9b0"),
            "struct name" => new Color("4ec9b0"),
            "interface name" => new Color("b8d7a3"),
            "namespace name" => new Color("dcdcdc"),
            
            // Identifiers & members
            "identifier" => new Color("dcdcdc"),
            "method name" => new Color("dcdcaa"),
            "extension method name" => new Color("dcdcaa"),
            "property name" => new Color("dcdcdc"),
            "static symbol" => new Color("dcdcaa"),
            "parameter name" => new Color("9cdcfe"),
            "local name" => new Color("9cdcfe"),

            // Punctuation & operators
            "operator" => new Color("dcdcdc"),
            "punctuation" => new Color("dcdcdc"),

            // Misc
            "excluded code" => new Color("a9a9a9"),

            _ => new Color("dcdcdc")
        };
    }
}
