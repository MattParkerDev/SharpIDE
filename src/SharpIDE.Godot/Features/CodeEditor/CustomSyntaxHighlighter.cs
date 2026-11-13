using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Godot;
using Godot.Collections;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Godot.Features.CodeEditor;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot;

public partial class CustomHighlighter : SyntaxHighlighter
{
    private readonly Dictionary _emptyDict = new();

    // All of the document's spans, sorted by line then character, and the range of them that belongs to each line that has any
    private ImmutableArray<SharpIdeClassifiedSpan> _classifiedSpans = [];
    private System.Collections.Generic.Dictionary<int, LineSpanRange> _spanRangesByLine = [];

    private readonly record struct LineSpanRange(int Start, int Count);

    public EditorThemeColorSet ColourSetForTheme = null!;
    private HighlightEntries _highlightEntries = null!;

    /// <summary>
    /// The per-colour { "color": Color } entries we return to Godot, shared by every line and editor using the same theme.
    /// Godot only reads them, so they are created once per theme instead of once per span on every highlight call.
    /// </summary>
    private sealed class HighlightEntries(Dictionary[] byClassification, Dictionary defaultColor)
    {
        public readonly Dictionary[] ByClassification = byClassification;
        public readonly Dictionary DefaultColor = defaultColor;
    }
    private static readonly ConditionalWeakTable<EditorThemeColorSet, HighlightEntries> _highlightEntriesByTheme = new();

    public void UpdateThemeColorCache(LightOrDarkTheme themeType)
    {
        ColourSetForTheme = themeType switch
        {
            LightOrDarkTheme.Light => EditorThemeColours.Light,
            LightOrDarkTheme.Dark => EditorThemeColours.Dark,
            _ => throw new NotImplementedException("Unknown theme type")
        };
        _highlightEntries = _highlightEntriesByTheme.GetValue(ColourSetForTheme, static colorSet =>
        {
            var colors = ClassificationToColorMapper.GetColorTable(colorSet);
            var byClassification = new Dictionary[colors.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                byClassification[i] = CreateEntry(colors[i]);
            }
            return new HighlightEntries(byClassification, CreateEntry(colorSet.White));

            static Dictionary CreateEntry(Color color) => new() { { ColorStringName, color } };
        });
    }

    /// <param name="classifiedSpans">Sorted, single-line, non-overlapping spans</param>
    public void SetHighlightingData(ImmutableArray<SharpIdeClassifiedSpan> classifiedSpans)
    {
        var spanRangesByLine = new System.Collections.Generic.Dictionary<int, LineSpanRange>();
        var lineStart = 0;
        for (var i = 1; i <= classifiedSpans.Length; i++)
        {
            if (i < classifiedSpans.Length && classifiedSpans[i].Line == classifiedSpans[lineStart].Line) continue;
            spanRangesByLine[classifiedSpans[lineStart].Line] = new LineSpanRange(lineStart, i - lineStart);
            lineStart = i;
        }

        _classifiedSpans = classifiedSpans;
        _spanRangesByLine = spanRangesByLine;
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
        _spanRangesByLine = Rearrange(_spanRangesByLine, fromLine, difference, origin);
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
        _spanRangesByLine = Rearrange(_spanRangesByLine, fromLine, numberOfLinesRemoved);
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
    
    private static readonly StringName ColorStringName = "color";

    public override Dictionary _GetLineSyntaxHighlighting(int line)
    {
        if (_spanRangesByLine.TryGetValue(line, out var range) is false) return _emptyDict;
        var spansForLine = _classifiedSpans.AsSpan(range.Start, range.Count);

        var entries = _highlightEntries.ByClassification;
        var defaultEntry = _highlightEntries.DefaultColor;
        var highlights = new Dictionary();
        for (var i = 0; i < spansForLine.Length; i++)
        {
            var span = spansForLine[i];
            if (span.Length is 0) continue;
            highlights[span.StartCharacter] = entries[(int)span.Classification];

            // Godot applies a colour until the next entry on the line, so reset to the default colour for any gap after this span
            var nextStart = i + 1 < spansForLine.Length ? spansForLine[i + 1].StartCharacter : int.MaxValue;
            if (nextStart > span.EndCharacter)
            {
                highlights[span.EndCharacter] = defaultEntry;
            }
        }

        return highlights;
    }
}
