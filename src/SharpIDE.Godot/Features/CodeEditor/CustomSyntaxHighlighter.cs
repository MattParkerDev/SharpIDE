using System.Collections.Immutable;
using Godot;
using Godot.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Godot.Features.CodeEditor;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot;

public partial class CustomHighlighter : SyntaxHighlighter
{
    private readonly Dictionary _emptyDict = new();

    private System.Collections.Generic.Dictionary<int, ImmutableArray<SharpIdeRazorClassifiedSpan>> _razorClassifiedSpansByLine = [];
    private System.Collections.Generic.Dictionary<int, ImmutableArray<SharpIdeClassifiedSpan>> _classifiedSpansByLine = [];

    public EditorThemeColorSet ColourSetForTheme = null!;
    
    public void UpdateThemeColorCache(LightOrDarkTheme themeType)
    {
        ColourSetForTheme = themeType switch
        {
            LightOrDarkTheme.Light => EditorThemeColours.Light,
            LightOrDarkTheme.Dark => EditorThemeColours.Dark,
            _ => throw new NotImplementedException("Unknown theme type")
        };
    }
    
    private Color ApplyTransparency(Color color)
    {
        var transparency = (float)Singletons.AppState.IdeSettings.CodeBackgroundTransparency;
        color.A = 1f - transparency;
        return color;
    }
    
    
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
                // StartOfLine: the new blank line was inserted *before* the existing content on fromLine,
                //              so fromLine's old content has shifted down — shift it.
                // EndOfLine:   the caret was at the end; the new line is blank and below; fromLine keeps its content.
                // MidLine:     the content before the caret stays on fromLine; only lines after shift.
                //              Treat the same as EndOfLine — fromLine's highlighting data stays put.
                // Unknown:     we don't know; conservatively keep fromLine's data in place (same as EndOfLine).
                bool shouldShift =
                    kvp.Key > fromLine ||
                    (origin == SharpIdeCodeEdit.LineEditOrigin.StartOfLine && kvp.Key == fromLine);

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
                { ColorStringName, ApplyTransparency(GetColorForRazorSpanKind(razorSpan.Kind, razorSpan.CodeClassificationType, razorSpan.VsSemanticRangeType)) }
            };

            highlights[columnIndex] = highlightInfo;
        }

        return highlights;
    }
    
    private Color GetColorForRazorSpanKind(SharpIdeRazorSpanKind kind, string? codeClassificationType, string? vsSemanticRangeType)
    {
        return kind switch
        {
            SharpIdeRazorSpanKind.Code => ClassificationToColorMapper.GetColorForClassification(ColourSetForTheme, codeClassificationType!),
            SharpIdeRazorSpanKind.Comment => ColourSetForTheme.CommentGreen, // green
            SharpIdeRazorSpanKind.MetaCode => ColourSetForTheme.RazorMetaCodePurple, // purple
            SharpIdeRazorSpanKind.Markup => GetColorForMarkupSpanKind(vsSemanticRangeType),
            SharpIdeRazorSpanKind.Transition => ColourSetForTheme.RazorMetaCodePurple, // purple
            SharpIdeRazorSpanKind.None => ColourSetForTheme.White,
            _ => ColourSetForTheme.White
        };
    }
    
    private Color GetColorForMarkupSpanKind(string? vsSemanticRangeType)
    {
        return vsSemanticRangeType switch
        {
            "razorDirective" or "razorTransition" => ColourSetForTheme.RazorMetaCodePurple, // purple
            "markupTagDelimiter" => ColourSetForTheme.HtmlDelimiterGray, // gray
            "markupTextLiteral" => ColourSetForTheme.White, // white
            "markupElement" => ColourSetForTheme.KeywordBlue, // blue
            "razorComponentElement" => ColourSetForTheme.RazorComponentGreen, // dark green
            "razorComponentAttribute" => ColourSetForTheme.White, // white
            "razorComment" or "razorCommentStar" or "razorCommentTransition" => ColourSetForTheme.CommentGreen, // green
            "markupOperator" => ColourSetForTheme.White, // white
            "markupAttributeQuote" => ColourSetForTheme.White, // white
            _ => ColourSetForTheme.White // default to white
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
                { ColorStringName, ApplyTransparency(ClassificationToColorMapper.GetColorForClassification(ColourSetForTheme, classifiedSpans.Single().ClassificationType)) }
            };

            highlights[columnIndex] = highlightInfo;
        }

        return highlights;
    }
    
    
}
