using Godot;
using SharpIDE.Application.Features.Git;
using SharpIDE.Godot.Features.CodeEditor;

namespace SharpIDE.Godot.Features.Git;

internal enum GitDiffMetricMode
{
    Primary,
    StagedOverlay
}

internal sealed class GitDiffLayoutMetrics(Control coordinateSpace, SharpIdeCodeEdit? baseEditor, SharpIdeCodeEdit? currentEditor, Control? actionGutter = null)
{
    public bool TryGetEditorBounds(GitDiffEditorSide side, out Rect2 bounds)
    {
        var editor = side is GitDiffEditorSide.Left ? baseEditor : currentEditor;
        if (editor is null)
        {
            bounds = default;
            return false;
        }

        bounds = new Rect2(editor.GlobalPosition - coordinateSpace.GlobalPosition, editor.Size);
        return true;
    }

    public bool TryGetActionGutterBounds(out Rect2 bounds)
    {
        if (actionGutter is null)
        {
            bounds = default;
            return false;
        }

        bounds = new Rect2(actionGutter.GlobalPosition - coordinateSpace.GlobalPosition, actionGutter.Size);
        return true;
    }

    public bool TryGetActionGutterLayout(out GitDiffActionGutterLayout layout)
    {
        if (!TryGetActionGutterBounds(out var bounds))
        {
            layout = default;
            return false;
        }

        layout = GitDiffActionGutterLayout.Create(bounds);
        return true;
    }

    public bool TryGetVisibleSegmentGeometry(GitDiffVisualSegment segment, GitDiffMetricMode mode, out GitDiffVisualSegmentGeometry geometry)
    {
        geometry = default;
        TryGetEditorBounds(GitDiffEditorSide.Left, out var leftEditorBounds);
        TryGetEditorBounds(GitDiffEditorSide.Right, out var rightEditorBounds);

        float? leftTop = null;
        float? leftBottom = null;
        float? rightTop = null;
        float? rightBottom = null;
        float? leftVisibleTop = null;
        float? leftVisibleBottom = null;
        float? rightVisibleTop = null;
        float? rightVisibleBottom = null;
        float leftAnchorX = leftEditorBounds.End.X;
        float? rightAnchorX = null;
        var hasVisibleGeometry = false;
        var leftHasVisibleGeometry = false;
        var rightHasVisibleGeometry = false;

        foreach (var row in segment.Rows)
        {
            if (!TryGetRowGeometry(row, mode, out var rowGeometry))
            {
                continue;
            }

            if (rowGeometry.LeftRect.HasValue)
            {
                var rect = rowGeometry.LeftRect.Value;
                leftTop = leftTop.HasValue ? Math.Min(leftTop.Value, rect.Position.Y) : rect.Position.Y;
                leftBottom = leftBottom.HasValue ? Math.Max(leftBottom.Value, rect.End.Y) : rect.End.Y;
                if (TryClipRectToVisible(rect, leftEditorBounds, out var visibleRect))
                {
                    hasVisibleGeometry = true;
                    leftHasVisibleGeometry = true;
                    leftVisibleTop = leftVisibleTop.HasValue ? Math.Min(leftVisibleTop.Value, visibleRect.Position.Y) : visibleRect.Position.Y;
                    leftVisibleBottom = leftVisibleBottom.HasValue ? Math.Max(leftVisibleBottom.Value, visibleRect.End.Y) : visibleRect.End.Y;
                }
            }

            if (rowGeometry.RightRect.HasValue)
            {
                var rect = rowGeometry.RightRect.Value;
                rightTop = rightTop.HasValue ? Math.Min(rightTop.Value, rect.Position.Y) : rect.Position.Y;
                rightBottom = rightBottom.HasValue ? Math.Max(rightBottom.Value, rect.End.Y) : rect.End.Y;
                rightAnchorX = rightAnchorX.HasValue ? Math.Min(rightAnchorX.Value, rect.Position.X) : rect.Position.X;
                if (TryClipRectToVisible(rect, rightEditorBounds, out var visibleRect))
                {
                    hasVisibleGeometry = true;
                    rightHasVisibleGeometry = true;
                    rightVisibleTop = rightVisibleTop.HasValue ? Math.Min(rightVisibleTop.Value, visibleRect.Position.Y) : visibleRect.Position.Y;
                    rightVisibleBottom = rightVisibleBottom.HasValue ? Math.Max(rightVisibleBottom.Value, visibleRect.End.Y) : visibleRect.End.Y;
                }
            }
        }

        if (!hasVisibleGeometry || (!leftTop.HasValue && !rightTop.HasValue))
        {
            return false;
        }

        if (leftHasVisibleGeometry)
        {
            leftTop = leftVisibleTop;
            leftBottom = leftVisibleBottom;
        }

        if (rightHasVisibleGeometry)
        {
            rightTop = rightVisibleTop;
            rightBottom = rightVisibleBottom;
        }

        geometry = new GitDiffVisualSegmentGeometry(
            LeftTop: leftTop,
            LeftBottom: leftBottom,
            RightTop: rightTop,
            RightBottom: rightBottom,
            LeftAnchorX: leftAnchorX,
            RightAnchorX: rightAnchorX ?? rightEditorBounds.Position.X,
            HasVisibleLeft: leftHasVisibleGeometry,
            HasVisibleRight: rightHasVisibleGeometry);
        return true;
    }

    public bool TryGetRowGeometry(GitDiffDisplayRow row, GitDiffMetricMode mode, out GitDiffRowGeometry geometry)
    {
        Rect2? leftRect = null;
        Rect2? rightRect = null;
        if (row.LeftFileLineNumber.HasValue && ShowsLeftSide(row.Kind) && TryGetEditorLineRect(baseEditor, row.LeftFileLineNumber.Value, out var leftLineRect))
        {
            leftRect = leftLineRect;
        }

        if (row.RightFileLineNumber.HasValue && ShowsRightSide(row.Kind) && TryGetEditorLineRect(currentEditor, row.RightFileLineNumber.Value, out var rightLineRect))
        {
            rightRect = rightLineRect;
        }

        if (!leftRect.HasValue && !rightRect.HasValue)
        {
            geometry = default;
            return false;
        }

        geometry = new GitDiffRowGeometry(leftRect, rightRect);
        return true;
    }

    public bool TryGetRowMetrics(GitDiffDisplayRow row, GitDiffMetricMode mode, out float y, out float rowHeight)
    {
        y = 0f;
        rowHeight = 0f;
        if (!TryGetRowGeometry(row, mode, out var geometry))
        {
            return false;
        }

        var points = new List<(float Y, float Height)>(2);
        if (geometry.LeftRect.HasValue)
        {
            points.Add((geometry.LeftRect.Value.Position.Y, geometry.LeftRect.Value.Size.Y));
        }

        if (geometry.RightRect.HasValue)
        {
            points.Add((geometry.RightRect.Value.Position.Y, geometry.RightRect.Value.Size.Y));
        }

        if (points.Count is 0)
        {
            return false;
        }

        y = points.Average(static point => point.Y);
        rowHeight = points.Average(static point => point.Height);
        return true;
    }

    public bool TryGetChunkMetrics(GitDiffViewModel diffView, GitDiffChunk chunk, GitDiffMetricMode mode, out float top, out float height, out GitDiffChunkBackgroundKind backgroundKind)
    {
        top = 0f;
        height = 0f;
        backgroundKind = GitDiffChunkBackgroundKind.None;

        float? minTop = null;
        float? maxBottom = null;
        foreach (var row in diffView.Rows.Where(row =>
                     string.Equals(row.ChunkId, chunk.ChunkId, StringComparison.Ordinal) &&
                     row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None))
        {
            if (!TryGetRowMetrics(row, mode, out var rowTop, out var rowHeight))
            {
                continue;
            }

            minTop = minTop.HasValue ? Math.Min(minTop.Value, rowTop) : rowTop;
            maxBottom = maxBottom.HasValue ? Math.Max(maxBottom.Value, rowTop + rowHeight) : rowTop + rowHeight;
            if (backgroundKind is GitDiffChunkBackgroundKind.None && row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None)
            {
                backgroundKind = row.ChunkBackgroundKind;
            }
        }

        if (!minTop.HasValue || !maxBottom.HasValue)
        {
            return false;
        }

        top = minTop.Value;
        height = Math.Max(14f, maxBottom.Value - minTop.Value);
        return true;
    }

    public bool TryGetInsertionMarkerRect(GitDiffViewModel diffView, GitDiffVisualSegment segment, GitDiffMetricMode mode, GitDiffEditorSide side, Rect2 bounds, out Rect2 markerRect)
    {
        markerRect = default;
        if (!TryGetGapAnchorPosition(diffView, segment.FirstDisplayRow, segment.LastDisplayRow, mode, side, out var centerY, out var height))
        {
            return false;
        }

        markerRect = new Rect2(
            new Vector2(bounds.Position.X, centerY - 1f),
            new Vector2(bounds.Size.X, 2f));
        return true;
    }

    public bool TryGetCurrentSideActionMetrics(GitDiffViewModel diffView, GitDiffDisplayRow row, GitDiffMetricMode mode, out float y, out float rowHeight)
    {
        y = 0f;
        rowHeight = 0f;

        if (row.RightFileLineNumber.HasValue &&
            TryGetRowGeometry(row, mode, out var rowGeometry) &&
            rowGeometry.RightRect.HasValue)
        {
            var rightRect = rowGeometry.RightRect.Value;
            rowHeight = rightRect.Size.Y;
            y = rightRect.Position.Y;
            return true;
        }

        if (row.LeftFileLineNumber.HasValue &&
            TryGetRowGeometry(row, mode, out rowGeometry) &&
            rowGeometry.LeftRect.HasValue)
        {
            var leftRect = rowGeometry.LeftRect.Value;
            var hasPrevious = TryGetPreviousAnchorSideLineRect(diffView, row.DisplayIndex, GitDiffEditorSide.Right, out var previousRect);
            var hasNext = TryGetNextAnchorSideLineRect(diffView, row.DisplayIndex, GitDiffEditorSide.Right, out var nextRect);

            if (hasPrevious && hasNext)
            {
                var leftCenter = leftRect.GetCenter().Y;
                var previousDistance = Math.Abs(leftCenter - previousRect.GetCenter().Y);
                var nextDistance = Math.Abs(leftCenter - nextRect.GetCenter().Y);
                var targetRect = previousDistance <= nextDistance ? previousRect : nextRect;
                rowHeight = targetRect.Size.Y;
                y = targetRect.Position.Y;
                return true;
            }

            if (hasNext)
            {
                rowHeight = nextRect.Size.Y;
                y = nextRect.Position.Y;
                return true;
            }

            if (hasPrevious)
            {
                rowHeight = previousRect.Size.Y;
                y = previousRect.Position.Y;
                return true;
            }
        }

        if (row.LeftFileLineNumber.HasValue &&
            TryGetGapAnchorPosition(diffView, row.DisplayIndex, row.DisplayIndex, mode, GitDiffEditorSide.Right, out var centerY, out var height))
        {
            rowHeight = Math.Max(12f, height);
            y = centerY - (rowHeight * 0.5f);
            return true;
        }

        return TryGetRowMetrics(row, mode, out y, out rowHeight);
    }

    public bool TryGetChunkActionMetrics(
        GitDiffViewModel diffView,
        GitDiffDisplayRow firstRow,
        GitDiffDisplayRow lastRow,
        GitDiffMetricMode mode,
        GitDiffEditorSide preferredSide,
        out float y,
        out float rowHeight)
    {
        y = 0f;
        rowHeight = 0f;

        foreach (var row in diffView.Rows)
        {
            if (row.DisplayIndex < firstRow.DisplayIndex || row.DisplayIndex > lastRow.DisplayIndex)
            {
                continue;
            }

            if (!TryGetSideActionMetrics(row, mode, preferredSide, out y, out rowHeight))
            {
                continue;
            }

            return true;
        }

        var targetCenter = ResolveChunkTargetCenter(firstRow, lastRow, mode);
        if (!TryGetNearestVisibleLineRect(diffView, firstRow.DisplayIndex, lastRow.DisplayIndex, preferredSide, targetCenter, out var nearestRect))
        {
            return false;
        }

        y = nearestRect.Position.Y;
        rowHeight = nearestRect.Size.Y;
        return true;
    }

    public IReadOnlyList<GitDiffGutterSpan> CollectVisibleGutterSpans(
        GitDiffViewModel diffView,
        GitDiffVisualSegment segment,
        GitDiffMetricMode mode,
        GitDiffActionGutterLayout layout,
        GitDiffEditorSide side,
        bool isStaged)
    {
        var spans = new List<GitDiffGutterSpan>();
        var bandBounds = side is GitDiffEditorSide.Left ? layout.LeftBandBounds : layout.RightBandBounds;

        int? pendingInsertionStart = null;
        int? pendingInsertionEnd = null;

        void FlushInsertionMarker()
        {
            if (!pendingInsertionStart.HasValue || !pendingInsertionEnd.HasValue)
            {
                return;
            }

            if (TryGetGapAnchorPosition(
                    diffView,
                    pendingInsertionStart.Value,
                    pendingInsertionEnd.Value,
                    mode,
                    side,
                    out var centerY,
                    out _) &&
                TryClipRectToVisible(
                    new Rect2(
                        new Vector2(bandBounds.Position.X, centerY - 1f),
                        new Vector2(bandBounds.Size.X, 2f)),
                    bandBounds,
                    out var clippedMarkerRect))
            {
                spans.Add(new GitDiffGutterSpan(clippedMarkerRect, segment.BackgroundKind, isStaged, IsInsertion: true));
            }

            pendingInsertionStart = null;
            pendingInsertionEnd = null;
        }

        foreach (var row in segment.Rows)
        {
            if (!TryGetRowGeometry(row, mode, out var rowGeometry))
            {
                continue;
            }

            var rowRect = side is GitDiffEditorSide.Left ? rowGeometry.LeftRect : rowGeometry.RightRect;
            if (!rowRect.HasValue)
            {
                pendingInsertionStart ??= row.DisplayIndex;
                pendingInsertionEnd = row.DisplayIndex;
                continue;
            }

            FlushInsertionMarker();

            var bandRect = new Rect2(
                new Vector2(bandBounds.Position.X, rowRect.Value.Position.Y),
                new Vector2(bandBounds.Size.X, rowRect.Value.Size.Y));
            if (TryClipRectToVisible(bandRect, bandBounds, out var clippedRect))
            {
                spans.Add(new GitDiffGutterSpan(clippedRect, segment.BackgroundKind, isStaged, IsInsertion: false));
            }
        }

        FlushInsertionMarker();

        return spans;
    }

    public IReadOnlyList<GitDiffVisibleEditorLine> CollectVisibleEditorLines(GitDiffEditorSide side)
    {
        var lines = new List<GitDiffVisibleEditorLine>();
        var editor = side is GitDiffEditorSide.Left ? baseEditor : currentEditor;
        if (editor is null || !TryGetEditorBounds(side, out var visibleBounds))
        {
            return lines;
        }

        var lineCount = editor.GetLineCount();
        if (lineCount <= 0)
        {
            return lines;
        }

        var lineHeight = Math.Max(1f, editor.GetLineHeight());
        var firstVisibleLine = Mathf.Clamp(editor.GetFirstVisibleLine(), 0, Math.Max(lineCount - 1, 0));
        var visibleLineEstimate = (int)Math.Ceiling(visibleBounds.Size.Y / lineHeight) + 2;
        var lastCandidateLine = Math.Min(lineCount - 1, firstVisibleLine + visibleLineEstimate);
        for (var lineIndex = firstVisibleLine; lineIndex <= lastCandidateLine; lineIndex++)
        {
            if (!TryGetEditorLineRect(editor, lineIndex + 1, out var rect) ||
                !TryClipRectToVisible(rect, visibleBounds, out _))
            {
                continue;
            }

            lines.Add(new GitDiffVisibleEditorLine(lineIndex + 1, rect));
        }

        return lines;
    }

    public GitDiffLayoutSnapshot CaptureSnapshot()
    {
        return new GitDiffLayoutSnapshot(
            CoordinateSpacePosition: coordinateSpace.GlobalPosition,
            CoordinateSpaceSize: coordinateSpace.Size,
            BaseEditorPosition: baseEditor?.GlobalPosition ?? Vector2.Zero,
            BaseEditorSize: baseEditor?.Size ?? Vector2.Zero,
            CurrentEditorPosition: currentEditor?.GlobalPosition ?? Vector2.Zero,
            CurrentEditorSize: currentEditor?.Size ?? Vector2.Zero,
            ActionGutterPosition: actionGutter?.GlobalPosition ?? Vector2.Zero,
            ActionGutterSize: actionGutter?.Size ?? Vector2.Zero,
            BaseScroll: baseEditor?.GetVScroll() ?? 0d,
            CurrentScroll: currentEditor?.GetVScroll() ?? 0d,
            BaseFirstVisibleLine: baseEditor?.GetFirstVisibleLine() ?? 0,
            CurrentFirstVisibleLine: currentEditor?.GetFirstVisibleLine() ?? 0);
    }

    public static IReadOnlyList<GitDiffVisualSegment> BuildVisualSegments(
        GitDiffViewModel diffView,
        Func<GitDiffDisplayRow, bool>? includeRow = null)
    {
        var segments = new List<GitDiffVisualSegment>();
        var buffer = new List<GitDiffDisplayRow>();
        GitDiffChunkBackgroundKind currentKind = GitDiffChunkBackgroundKind.None;
        string? currentChunkId = null;
        var previousDisplayIndex = -1;

        void Flush()
        {
            if (buffer.Count is 0)
            {
                return;
            }

            segments.Add(new GitDiffVisualSegment
            {
                BackgroundKind = currentKind,
                FirstDisplayRow = buffer[0].DisplayIndex,
                LastDisplayRow = buffer[^1].DisplayIndex,
                Rows = buffer.ToArray()
            });
            buffer.Clear();
            currentKind = GitDiffChunkBackgroundKind.None;
            currentChunkId = null;
            previousDisplayIndex = -1;
        }

        foreach (var row in diffView.Rows)
        {
            if (row.ChunkBackgroundKind is GitDiffChunkBackgroundKind.None ||
                (includeRow is not null && !includeRow(row)))
            {
                Flush();
                continue;
            }

            var startsNewSegment = buffer.Count is not 0 &&
                                   (row.DisplayIndex != previousDisplayIndex + 1 ||
                                    row.ChunkBackgroundKind != currentKind ||
                                    !string.Equals(row.ChunkId, currentChunkId, StringComparison.Ordinal));
            if (startsNewSegment)
            {
                Flush();
            }

            if (buffer.Count is 0)
            {
                currentKind = row.ChunkBackgroundKind;
                currentChunkId = row.ChunkId;
            }

            buffer.Add(row);
            previousDisplayIndex = row.DisplayIndex;
        }

        Flush();
        return segments;
    }

    private bool TryGetSideActionMetrics(
        GitDiffDisplayRow row,
        GitDiffMetricMode mode,
        GitDiffEditorSide side,
        out float y,
        out float rowHeight)
    {
        y = 0f;
        rowHeight = 0f;
        if (!TryGetRowGeometry(row, mode, out var rowGeometry))
        {
            return false;
        }

        var rect = side is GitDiffEditorSide.Left ? rowGeometry.LeftRect : rowGeometry.RightRect;
        if (!rect.HasValue)
        {
            return false;
        }

        y = rect.Value.Position.Y;
        rowHeight = rect.Value.Size.Y;
        return true;
    }

    private float ResolveChunkTargetCenter(GitDiffDisplayRow firstRow, GitDiffDisplayRow lastRow, GitDiffMetricMode mode)
    {
        if (TryGetRowMetrics(firstRow, mode, out var firstY, out var firstHeight) &&
            TryGetRowMetrics(lastRow, mode, out var lastY, out var lastHeight))
        {
            var firstCenter = firstY + (firstHeight * 0.5f);
            var lastCenter = lastY + (lastHeight * 0.5f);
            return (firstCenter + lastCenter) * 0.5f;
        }

        if (TryGetRowMetrics(firstRow, mode, out firstY, out firstHeight))
        {
            return firstY + (firstHeight * 0.5f);
        }

        return TryGetRowMetrics(lastRow, mode, out lastY, out lastHeight)
            ? lastY + (lastHeight * 0.5f)
            : 0f;
    }

    private bool TryGetNearestVisibleLineRect(
        GitDiffViewModel diffView,
        int firstDisplayRow,
        int lastDisplayRow,
        GitDiffEditorSide side,
        float targetCenter,
        out Rect2 rect)
    {
        rect = default;
        var hasPrevious = TryGetPreviousAnchorSideLineRect(diffView, firstDisplayRow, side, out var previousRect);
        var hasNext = TryGetNextAnchorSideLineRect(diffView, lastDisplayRow, side, out var nextRect);
        if (hasPrevious && hasNext)
        {
            var previousDistance = Math.Abs(targetCenter - previousRect.GetCenter().Y);
            var nextDistance = Math.Abs(targetCenter - nextRect.GetCenter().Y);
            rect = previousDistance <= nextDistance ? previousRect : nextRect;
            return true;
        }

        if (hasPrevious)
        {
            rect = previousRect;
            return true;
        }

        if (!hasNext)
        {
            return false;
        }

        rect = nextRect;
        return true;
    }

    private bool TryGetEditorLineRect(SharpIdeCodeEdit? editor, int lineNumber1Based, out Rect2 rect)
    {
        rect = default;
        if (editor is null)
        {
            return false;
        }

        var lineIndex = lineNumber1Based - 1;
        if (lineIndex < 0 || lineIndex >= editor.GetLineCount())
        {
            return false;
        }

        var anchorLineIndex = Mathf.Clamp(editor.GetFirstVisibleLine(), 0, Math.Max(editor.GetLineCount() - 1, 0));
        var anchorRect = editor.GetRectAtLineColumn(anchorLineIndex, 0);
        var lineHeight = Math.Max(1f, editor.GetLineHeight());
        var lineOffset = (lineIndex - anchorLineIndex) * lineHeight;
        var top = editor.GlobalPosition.Y - coordinateSpace.GlobalPosition.Y + anchorRect.Position.Y + lineOffset;
        var startX = Mathf.Round(editor.GlobalPosition.X - coordinateSpace.GlobalPosition.X);
        var width = Math.Max(1f, Mathf.Round(editor.Size.X));
        rect = new Rect2(
            new Vector2(startX, top),
            new Vector2(width, lineHeight));
        return true;
    }

    private bool TryGetGapAnchorPosition(
        GitDiffViewModel diffView,
        int firstDisplayRow,
        int lastDisplayRow,
        GitDiffMetricMode mode,
        GitDiffEditorSide side,
        out float centerY,
        out float height)
    {
        centerY = 0f;
        height = 0f;

        Rect2? previousRect = null;
        Rect2? nextRect = null;
        foreach (var row in diffView.Rows)
        {
            if (row.DisplayIndex >= firstDisplayRow)
            {
                break;
            }

            if (TryGetAnchorSideLineRect(row, side, out var sideRect))
            {
                previousRect = sideRect;
            }
        }

        foreach (var row in diffView.Rows)
        {
            if (row.DisplayIndex <= lastDisplayRow)
            {
                continue;
            }

            if (TryGetAnchorSideLineRect(row, side, out var sideRect))
            {
                nextRect = sideRect;
                break;
            }
        }

        if (!previousRect.HasValue && !nextRect.HasValue)
        {
            return false;
        }

        if (previousRect.HasValue && nextRect.HasValue)
        {
            centerY = (previousRect.Value.End.Y + nextRect.Value.Position.Y) * 0.5f;
            height = (previousRect.Value.Size.Y + nextRect.Value.Size.Y) * 0.5f;
        }
        else if (previousRect.HasValue)
        {
            centerY = previousRect.Value.End.Y;
            height = previousRect.Value.Size.Y;
        }
        else
        {
            centerY = nextRect!.Value.Position.Y;
            height = nextRect.Value.Size.Y;
        }

        return true;
    }

    private bool TryGetPreviousAnchorSideLineRect(GitDiffViewModel diffView, int beforeDisplayRow, GitDiffEditorSide side, out Rect2 rect)
    {
        rect = default;
        var found = false;
        foreach (var row in diffView.Rows)
        {
            if (row.DisplayIndex >= beforeDisplayRow)
            {
                break;
            }

            if (TryGetAnchorSideLineRect(row, side, out var sideRect))
            {
                rect = sideRect;
                found = true;
            }
        }

        return found;
    }

    private bool TryGetNextAnchorSideLineRect(GitDiffViewModel diffView, int afterDisplayRow, GitDiffEditorSide side, out Rect2 rect)
    {
        rect = default;
        foreach (var row in diffView.Rows)
        {
            if (row.DisplayIndex <= afterDisplayRow)
            {
                continue;
            }

            if (TryGetAnchorSideLineRect(row, side, out var sideRect))
            {
                rect = sideRect;
                return true;
            }
        }

        return false;
    }

    private bool TryGetAnchorSideLineRect(GitDiffDisplayRow row, GitDiffEditorSide side, out Rect2 rect)
    {
        rect = default;
        var lineNumber = side is GitDiffEditorSide.Left ? row.LeftFileLineNumber : row.RightFileLineNumber;
        if (!lineNumber.HasValue)
        {
            return false;
        }

        var editor = side is GitDiffEditorSide.Left ? baseEditor : currentEditor;
        return TryGetEditorLineRect(editor, lineNumber.Value, out rect);
    }

    private static bool ShowsLeftSide(GitDiffDisplayRowKind kind)
    {
        return kind switch
        {
            GitDiffDisplayRowKind.Added => false,
            GitDiffDisplayRowKind.Context or GitDiffDisplayRowKind.Spacer => false,
            _ => true
        };
    }

    private static bool ShowsRightSide(GitDiffDisplayRowKind kind)
    {
        return kind switch
        {
            GitDiffDisplayRowKind.Removed => false,
            GitDiffDisplayRowKind.Context or GitDiffDisplayRowKind.Spacer => false,
            _ => true
        };
    }

    private static bool TryClipRectToVisible(Rect2 rect, Rect2 visibleBounds, out Rect2 clippedRect)
    {
        var top = Math.Max(rect.Position.Y, visibleBounds.Position.Y);
        var bottom = Math.Min(rect.End.Y, visibleBounds.End.Y);
        if (bottom <= top)
        {
            clippedRect = default;
            return false;
        }

        clippedRect = new Rect2(
            new Vector2(rect.Position.X, top),
            new Vector2(rect.Size.X, bottom - top));
        return true;
    }

    internal static IReadOnlyList<GitDiffGutterSpan> MergeGutterSpans(IReadOnlyList<GitDiffGutterSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        const float mergeTolerance = 1f;
        var orderedSpans = spans
            .OrderBy(static span => span.Rect.Position.X)
            .ThenBy(static span => span.Rect.Position.Y)
            .ToList();
        var merged = new List<GitDiffGutterSpan>(orderedSpans.Count);
        foreach (var span in orderedSpans)
        {
            if (merged.Count is 0)
            {
                merged.Add(span);
                continue;
            }

            var previous = merged[^1];
            var sameStyle = previous.Kind == span.Kind &&
                            previous.IsStaged == span.IsStaged &&
                            previous.IsInsertion == span.IsInsertion &&
                            Mathf.IsEqualApprox(previous.Rect.Position.X, span.Rect.Position.X) &&
                            Mathf.IsEqualApprox(previous.Rect.Size.X, span.Rect.Size.X);
            var overlaps = span.Rect.Position.Y <= previous.Rect.End.Y + mergeTolerance;
            if (!sameStyle || !overlaps)
            {
                merged.Add(span);
                continue;
            }

            var mergedTop = Math.Min(previous.Rect.Position.Y, span.Rect.Position.Y);
            var mergedBottom = Math.Max(previous.Rect.End.Y, span.Rect.End.Y);
            merged[^1] = previous with
            {
                Rect = new Rect2(
                    new Vector2(previous.Rect.Position.X, mergedTop),
                    new Vector2(previous.Rect.Size.X, mergedBottom - mergedTop))
            };
        }

        return merged;
    }
}

internal enum GitDiffEditorSide
{
    Left,
    Right
}

internal readonly record struct GitDiffLayoutSnapshot(
    Vector2 CoordinateSpacePosition,
    Vector2 CoordinateSpaceSize,
    Vector2 BaseEditorPosition,
    Vector2 BaseEditorSize,
    Vector2 CurrentEditorPosition,
    Vector2 CurrentEditorSize,
    Vector2 ActionGutterPosition,
    Vector2 ActionGutterSize,
    double BaseScroll,
    double CurrentScroll,
    int BaseFirstVisibleLine,
    int CurrentFirstVisibleLine);

internal readonly record struct GitDiffRowGeometry(Rect2? LeftRect, Rect2? RightRect)
{
    public bool HasLeft => LeftRect.HasValue;
    public bool HasRight => RightRect.HasValue;
}

internal readonly record struct GitDiffVisibleEditorLine(int LineNumber, Rect2 Rect);

internal readonly record struct GitDiffActionGutterLayout(
    Rect2 Bounds,
    Rect2 RevertColumnBounds,
    Rect2 LeftLineNumberBounds,
    Rect2 LeftBandBounds,
    Rect2 MiddleBandBounds,
    Rect2 DividerHitBounds,
    float LeftSeamX,
    float DividerX,
    float ConnectorCenterX,
    float RightSeamX,
    Rect2 RightBandBounds,
    Rect2 RightLineNumberBounds,
    Rect2 StageColumnBounds,
    float LeftBandInnerX,
    float RightBandInnerX,
    float LeftConnectorEntryX,
    float RightConnectorEntryX,
    float LeftNumberAnchorX,
    float RightNumberAnchorX)
{
    public const float RevertColumnWidth = 18f;
    public const float StageColumnWidth = 18f;
    public const float MiddleBandWidth = 40f;
    public const float DividerHitWidth = 16f;
    public const float MinNumberColumnWidth = 26f;
    public const float ConnectorInnerInset = 8f;

    public static GitDiffActionGutterLayout Create(Rect2 bounds)
    {
        var fixedWidth = RevertColumnWidth + MiddleBandWidth + StageColumnWidth;
        var numberWidth = Math.Max(MinNumberColumnWidth, (bounds.Size.X - fixedWidth) * 0.5f);
        var x = bounds.Position.X;
        var revertColumnBounds = new Rect2(new Vector2(x, bounds.Position.Y), new Vector2(RevertColumnWidth, bounds.Size.Y));
        x += RevertColumnWidth;
        var leftLineNumberBounds = new Rect2(new Vector2(x, bounds.Position.Y), new Vector2(numberWidth, bounds.Size.Y));
        x += numberWidth;
        var middleBandBounds = new Rect2(new Vector2(x, bounds.Position.Y), new Vector2(MiddleBandWidth, bounds.Size.Y));
        var leftBandBounds = new Rect2(
            bounds.Position,
            new Vector2(middleBandBounds.Position.X - bounds.Position.X, bounds.Size.Y));
        var rightBandBounds = new Rect2(
            new Vector2(middleBandBounds.End.X, bounds.Position.Y),
            new Vector2(bounds.End.X - middleBandBounds.End.X, bounds.Size.Y));
        var dividerHitBounds = new Rect2(
            new Vector2(middleBandBounds.GetCenter().X - (DividerHitWidth * 0.5f), bounds.Position.Y),
            new Vector2(DividerHitWidth, bounds.Size.Y));
        var leftSeamX = Mathf.Round(bounds.Position.X);
        var dividerX = Mathf.Round(middleBandBounds.GetCenter().X);
        var connectorCenterX = dividerX;
        var rightSeamX = Mathf.Round(bounds.End.X - 1f);
        var leftBandInnerX = Mathf.Round(middleBandBounds.Position.X);
        var rightBandInnerX = Mathf.Round(middleBandBounds.End.X);
        var leftConnectorEntryX = Mathf.Round(middleBandBounds.Position.X + ConnectorInnerInset);
        var rightConnectorEntryX = Mathf.Round(middleBandBounds.End.X - ConnectorInnerInset);
        var leftNumberAnchorX = leftLineNumberBounds.End.X - 2f;
        x += MiddleBandWidth;
        var rightLineNumberBounds = new Rect2(new Vector2(x, bounds.Position.Y), new Vector2(numberWidth, bounds.Size.Y));
        var rightNumberAnchorX = rightLineNumberBounds.Position.X + 2f;
        x += numberWidth;
        var stageColumnBounds = new Rect2(new Vector2(x, bounds.Position.Y), new Vector2(StageColumnWidth, bounds.Size.Y));
        return new GitDiffActionGutterLayout(
            bounds,
            revertColumnBounds,
            leftLineNumberBounds,
            leftBandBounds,
            middleBandBounds,
            dividerHitBounds,
            leftSeamX,
            dividerX,
            connectorCenterX,
            rightSeamX,
            rightBandBounds,
            rightLineNumberBounds,
            stageColumnBounds,
            leftBandInnerX,
            rightBandInnerX,
            leftConnectorEntryX,
            rightConnectorEntryX,
            leftNumberAnchorX,
            rightNumberAnchorX);
    }
}

internal readonly record struct GitDiffVisualSegmentGeometry(
    float? LeftTop,
    float? LeftBottom,
    float? RightTop,
    float? RightBottom,
    float LeftAnchorX,
    float RightAnchorX,
    bool HasVisibleLeft,
    bool HasVisibleRight)
{
    public bool HasLeft => LeftTop.HasValue;
    public bool HasRight => RightTop.HasValue;
    public float VisibleTop => Math.Min(LeftTop ?? float.MaxValue, RightTop ?? float.MaxValue);
    public float VisibleBottom => Math.Max(LeftBottom ?? float.MinValue, RightBottom ?? float.MinValue);
}

internal sealed class GitDiffVisualSegment
{
    public required GitDiffChunkBackgroundKind BackgroundKind { get; init; }
    public required int FirstDisplayRow { get; init; }
    public required int LastDisplayRow { get; init; }
    public required IReadOnlyList<GitDiffDisplayRow> Rows { get; init; }
}

internal readonly record struct GitDiffGutterSpan(
    Rect2 Rect,
    GitDiffChunkBackgroundKind Kind,
    bool IsStaged,
    bool IsInsertion);
