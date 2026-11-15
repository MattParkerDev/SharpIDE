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
    // Rebuilding the line index (new highlighting data, or lines added/removed) fills this, then swaps it with _spanRangesByLine,
    // so the two dictionaries are reused instead of allocating a new one each time
    private System.Collections.Generic.Dictionary<int, LineSpanRange> _spareSpanRangesByLine = [];

    private readonly record struct LineSpanRange(int Start, int Count);

    public EditorThemeColorSet ColourSetForTheme = null!;
    private HighlightEntries _highlightEntries = null!;

    /// <summary>
    /// The per-colour { "color": Color } entries we return to Godot, shared by every line and editor using the same theme.
    /// Godot only reads them, so they are created once per theme instead of once per span on every highlight call.
    /// </summary>
    private sealed class HighlightEntries(Dictionary[] byClassification, Dictionary defaultColor, Dictionary[] fadedByClassification, Dictionary fadedDefaultColor)
    {
        public readonly Dictionary[] ByClassification = byClassification;
        public readonly Dictionary DefaultColor = defaultColor;
        // For code marked unnecessary, e.g. unnecessary usings
        public readonly Dictionary[] FadedByClassification = fadedByClassification;
        public readonly Dictionary FadedDefaultColor = fadedDefaultColor;
    }
    // Similar to VS Code's editorUnnecessaryCode.opacity
    private const float FadedAlpha = 0.55f;

    // Column ranges to fade (End exclusive, int.MaxValue for the rest of the line), by line. Rare, so lines without any take the normal path.
    private System.Collections.Generic.Dictionary<int, List<(int Start, int End)>> _fadedRangesByLine = [];
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
            var fadedByClassification = new Dictionary[colors.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                byClassification[i] = CreateEntry(colors[i]);
                fadedByClassification[i] = CreateEntry(colors[i] with { A = colors[i].A * FadedAlpha });
            }
            return new HighlightEntries(byClassification, CreateEntry(colorSet.White), fadedByClassification, CreateEntry(colorSet.White with { A = colorSet.White.A * FadedAlpha }));

            static Dictionary CreateEntry(Color color) => new() { { ColorStringName, color } };
        });
    }

    /// <param name="classifiedSpans">Sorted, single-line, non-overlapping spans</param>
    public void SetHighlightingData(ImmutableArray<SharpIdeClassifiedSpan> classifiedSpans)
    {
        var spanRangesByLine = TakeClearedSpare();
        var lineStart = 0;
        for (var i = 1; i <= classifiedSpans.Length; i++)
        {
            if (i < classifiedSpans.Length && classifiedSpans[i].Line == classifiedSpans[lineStart].Line) continue;
            spanRangesByLine[classifiedSpans[lineStart].Line] = new LineSpanRange(lineStart, i - lineStart);
            lineStart = i;
        }

        _classifiedSpans = classifiedSpans;
        SwapInSpare();
    }

    /// The spans of code to fade out, e.g. unnecessary usings. Godot caches line highlighting, so callers must refresh it for already highlighted lines to change.
    public void SetFadedSpans(IEnumerable<Microsoft.CodeAnalysis.Text.LinePositionSpan> spans)
    {
        var fadedRangesByLine = new System.Collections.Generic.Dictionary<int, List<(int Start, int End)>>();
        foreach (var span in spans)
        {
            for (var line = span.Start.Line; line <= span.End.Line; line++)
            {
                var start = line == span.Start.Line ? span.Start.Character : 0;
                var end = line == span.End.Line ? span.End.Character : int.MaxValue;
                if (end <= start) continue;
                if (fadedRangesByLine.TryGetValue(line, out var ranges) is false) fadedRangesByLine[line] = ranges = [];
                ranges.Add((start, end));
            }
        }
        _fadedRangesByLine = fadedRangesByLine;
    }

    private System.Collections.Generic.Dictionary<int, LineSpanRange> TakeClearedSpare()
    {
        _spareSpanRangesByLine.Clear();
        return _spareSpanRangesByLine;
    }

    private void SwapInSpare() => (_spanRangesByLine, _spareSpanRangesByLine) = (_spareSpanRangesByLine, _spanRangesByLine);

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
        Rearrange(_spanRangesByLine, TakeClearedSpare(), fromLine, difference, origin);
        SwapInSpare();
        return;

        static void Rearrange(System.Collections.Generic.Dictionary<int, LineSpanRange> existingDictionary, System.Collections.Generic.Dictionary<int, LineSpanRange> newDict, long fromLine, int difference, SharpIdeCodeEdit.LineEditOrigin origin)
        {
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
        }
    }
    
    private void LinesRemoved(long fromLine, int numberOfLinesRemoved)
    {
        Rearrange(_spanRangesByLine, TakeClearedSpare(), fromLine, numberOfLinesRemoved);
        SwapInSpare();
        return;

        static void Rearrange(System.Collections.Generic.Dictionary<int, LineSpanRange> existingDictionary, System.Collections.Generic.Dictionary<int, LineSpanRange> newDict, long fromLine, int numberOfLinesRemoved)
        {
            // everything from 'fromLine' onwards needs to be shifted up by numberOfLinesRemoved
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
        }
    }
    
    private static readonly StringName ColorStringName = "color";

    public override Dictionary _GetLineSyntaxHighlighting(int line)
    {
        if (_fadedRangesByLine.TryGetValue(line, out var fadedRanges)) return GetFadedLineSyntaxHighlighting(line, fadedRanges);
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

    /// As _GetLineSyntaxHighlighting, but with the faded ranges drawn with the faded colours. Godot applies a colour from its column until the next entry,
    /// so an entry is needed at every point where either the classification or fading changes.
    private Dictionary GetFadedLineSyntaxHighlighting(int line, List<(int Start, int End)> fadedRanges)
    {
        // The classification (-1 for the default colour) starting at each column
        var classificationStarts = new SortedDictionary<int, int>();
        if (_spanRangesByLine.TryGetValue(line, out var range))
        {
            foreach (var span in _classifiedSpans.AsSpan(range.Start, range.Count))
            {
                if (span.Length is 0) continue;
                classificationStarts[span.StartCharacter] = (int)span.Classification;
                classificationStarts.TryAdd(span.EndCharacter, -1); // Reset to the default colour for any gap - a following span starting here overwrites it
            }
        }

        var boundaries = new SortedSet<int>(classificationStarts.Keys) { 0 };
        foreach (var (start, end) in fadedRanges)
        {
            boundaries.Add(start);
            if (end is not int.MaxValue) boundaries.Add(end);
        }

        var entries = _highlightEntries;
        var highlights = new Dictionary();
        var classification = -1;
        foreach (var column in boundaries)
        {
            if (classificationStarts.TryGetValue(column, out var startingClassification)) classification = startingClassification;
            var isFaded = fadedRanges.Exists(r => r.Start <= column && column < r.End);
            highlights[column] = (classification, isFaded) switch
            {
                (-1, false) => entries.DefaultColor,
                (-1, true) => entries.FadedDefaultColor,
                (_, false) => entries.ByClassification[classification],
                (_, true) => entries.FadedByClassification[classification]
            };
        }
        return highlights;
    }
}
