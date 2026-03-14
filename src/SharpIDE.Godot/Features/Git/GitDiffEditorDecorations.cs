using Godot;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Godot.Features.Git;

internal static class GitDiffEditorDecorations
{
    public static IReadOnlyDictionary<int, Color> BuildLineHighlights(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId,
        bool isLeftSide)
    {
        _ = rowStatesByRowId;
        var lineColors = new Dictionary<int, Color>();

        foreach (var row in diffView.Rows)
        {
            var lineNumber = isLeftSide ? row.LeftFileLineNumber : row.RightFileLineNumber;
            if (!lineNumber.HasValue)
            {
                continue;
            }

            var color = GitDiffPalette.GetLineBackground(row.Kind, isLeftSide);
            if (color == Colors.Transparent)
            {
                continue;
            }

            lineColors[lineNumber.Value - 1] = color;
        }

        return lineColors;
    }

    public static IReadOnlyDictionary<int, IReadOnlyList<GitDiffInlineDecoration>> BuildInlineHighlights(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId,
        bool isLeftSide)
    {
        _ = rowStatesByRowId;
        var highlights = new Dictionary<int, List<GitDiffInlineDecoration>>();

        foreach (var row in diffView.Rows)
        {
            var lineNumber = isLeftSide ? row.LeftFileLineNumber : row.RightFileLineNumber;
            var spans = isLeftSide ? row.InlineHighlightsLeft : row.InlineHighlightsRight;
            if (!lineNumber.HasValue || spans.Count is 0)
            {
                continue;
            }

            var lineIndex = lineNumber.Value - 1;
            if (!highlights.TryGetValue(lineIndex, out var lineHighlights))
            {
                lineHighlights = [];
                highlights[lineIndex] = lineHighlights;
            }

            foreach (var span in spans)
            {
                lineHighlights.Add(new GitDiffInlineDecoration(span, IsStaged: false));
            }
        }

        return highlights.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<GitDiffInlineDecoration>)pair.Value.ToArray());
    }
}

public readonly record struct GitDiffInlineDecoration(GitInlineHighlightSpan Span, bool IsStaged);
