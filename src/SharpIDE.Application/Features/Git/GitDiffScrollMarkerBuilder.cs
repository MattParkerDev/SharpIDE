namespace SharpIDE.Application.Features.Git;

public enum GitDiffScrollMarkerKind
{
    Added,
    Removed,
    Modified
}

public sealed class GitDiffScrollMarker
{
    public required GitDiffScrollMarkerKind Kind { get; init; }
    public required GitDiffRowStageState StageState { get; init; }
    public required int AnchorLine { get; init; }
    public required int TargetLine { get; init; }
    public required int LineSpan { get; init; }
}

public static class GitDiffScrollMarkerBuilder
{
    public static IReadOnlyList<GitDiffScrollMarker> BuildBaseDocumentMarkers(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId = null)
    {
        return BuildMarkers(diffView, rowStatesByRowId, useLeftSide: true);
    }

    public static IReadOnlyList<GitDiffScrollMarker> BuildCurrentDocumentMarkers(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId = null)
    {
        return BuildMarkers(diffView, rowStatesByRowId, useLeftSide: false);
    }

    private static IReadOnlyList<GitDiffScrollMarker> BuildMarkers(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId,
        bool useLeftSide)
    {
        ArgumentNullException.ThrowIfNull(diffView);

        var orderedRows = diffView.Rows.OrderBy(static row => row.DisplayIndex).ToArray();
        var rawMarkers = new List<GitDiffScrollMarker>();

        for (var index = 0; index < orderedRows.Length; index++)
        {
            var row = orderedRows[index];
            var sideLineNumber = useLeftSide ? row.LeftFileLineNumber : row.RightFileLineNumber;
            var kind = GetMarkerKind(row.Kind, useLeftSide);
            if (!kind.HasValue)
            {
                continue;
            }

            var targetLine = ResolveTargetLine(orderedRows, index, useLeftSide);
            var stageState = rowStatesByRowId is not null && rowStatesByRowId.TryGetValue(row.RowId, out var rowState)
                ? rowState.StageState
                : GitDiffRowStageState.None;

            rawMarkers.Add(new GitDiffScrollMarker
            {
                Kind = kind.Value,
                StageState = stageState,
                AnchorLine = Math.Max(targetLine, 0),
                TargetLine = targetLine,
                LineSpan = 1
            });
        }

        if (rawMarkers.Count is 0)
        {
            return [];
        }

        var groupedMarkers = new List<GitDiffScrollMarker>();
        var current = rawMarkers[0];
        var currentGroupStart = current.AnchorLine;
        var currentGroupEnd = current.AnchorLine;
        var currentGroupCount = current.LineSpan;

        foreach (var marker in rawMarkers.Skip(1))
        {
            var isContiguous = marker.AnchorLine <= currentGroupEnd + 1;
            if (marker.Kind == current.Kind &&
                marker.StageState == current.StageState &&
                isContiguous)
            {
                currentGroupEnd = Math.Max(currentGroupEnd, marker.AnchorLine);
                currentGroupCount += marker.LineSpan;
                continue;
            }

            groupedMarkers.Add(new GitDiffScrollMarker
            {
                Kind = current.Kind,
                StageState = current.StageState,
                AnchorLine = currentGroupStart,
                TargetLine = current.TargetLine,
                LineSpan = Math.Max(currentGroupEnd - currentGroupStart + 1, currentGroupCount)
            });

            current = marker;
            currentGroupStart = marker.AnchorLine;
            currentGroupEnd = marker.AnchorLine;
            currentGroupCount = marker.LineSpan;
        }

        groupedMarkers.Add(new GitDiffScrollMarker
        {
            Kind = current.Kind,
            StageState = current.StageState,
            AnchorLine = currentGroupStart,
            TargetLine = current.TargetLine,
            LineSpan = Math.Max(currentGroupEnd - currentGroupStart + 1, currentGroupCount)
        });

        return groupedMarkers;
    }

    private static int ResolveTargetLine(IReadOnlyList<GitDiffDisplayRow> orderedRows, int index, bool useLeftSide)
    {
        var row = orderedRows[index];
        var directLineNumber = useLeftSide ? row.LeftFileLineNumber : row.RightFileLineNumber;
        if (directLineNumber.HasValue)
        {
            return Math.Max(directLineNumber.Value - 1, 0);
        }

        for (var nextIndex = index + 1; nextIndex < orderedRows.Count; nextIndex++)
        {
            var nextLineNumber = useLeftSide ? orderedRows[nextIndex].LeftFileLineNumber : orderedRows[nextIndex].RightFileLineNumber;
            if (nextLineNumber.HasValue)
            {
                return Math.Max(nextLineNumber.Value - 1, 0);
            }
        }

        for (var previousIndex = index - 1; previousIndex >= 0; previousIndex--)
        {
            var previousLineNumber = useLeftSide ? orderedRows[previousIndex].LeftFileLineNumber : orderedRows[previousIndex].RightFileLineNumber;
            if (previousLineNumber.HasValue)
            {
                return Math.Max(previousLineNumber.Value, 0);
            }
        }

        return 0;
    }

    private static GitDiffScrollMarkerKind? GetMarkerKind(GitDiffDisplayRowKind kind, bool useLeftSide)
    {
        return (kind, useLeftSide) switch
        {
            (GitDiffDisplayRowKind.Added, false) => GitDiffScrollMarkerKind.Added,
            (GitDiffDisplayRowKind.Removed, true) => GitDiffScrollMarkerKind.Removed,
            (GitDiffDisplayRowKind.Removed, false) => GitDiffScrollMarkerKind.Removed,
            (GitDiffDisplayRowKind.ModifiedLeft, true) => GitDiffScrollMarkerKind.Modified,
            (GitDiffDisplayRowKind.ModifiedRight, true) => GitDiffScrollMarkerKind.Modified,
            (GitDiffDisplayRowKind.ModifiedRight, false) => GitDiffScrollMarkerKind.Modified,
            _ => null
        };
    }
}
