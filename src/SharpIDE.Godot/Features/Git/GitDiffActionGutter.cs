using Godot;
using SharpIDE.Application.Features.Git;
using SharpIDE.Godot.Features.CodeEditor;

namespace SharpIDE.Godot.Features.Git;

public partial class GitDiffActionGutter : Control
{
    private const float MinimumGutterWidth =
        GitDiffActionGutterLayout.RevertColumnWidth +
        GitDiffActionGutterLayout.StageColumnWidth +
        GitDiffActionGutterLayout.MiddleBandWidth +
        (GitDiffActionGutterLayout.MinNumberColumnWidth * 2f);
    private const float NumberColumnPadding = 10f;
    private const float ActionControlSize = 14f;
    private const float ActionControlVerticalOffset = 0f;
    private const float ActionControlInnerPadding = 0f;
    private static readonly Texture2D RevertChunkChevronIcon = GD.Load<Texture2D>("res://Features/Git/Resources/RevertChunkChevron.svg");

    public event Action<IReadOnlyList<string>>? StageLinesRequested;
    public event Action<IReadOnlyList<string>>? UnstageLinesRequested;
    public event Action<string>? StageChunkRequested;
    public event Action<string>? UnstageChunkRequested;
    public event Action<string>? RevertChunkRequested;
    public event Action<float>? DividerDragged;

    private readonly List<LineActionHotspot> _lineMarkerHotspots = [];
    private readonly List<ChunkActionHotspot> _chunkActionHotspots = [];
    private readonly List<HunkAreaHotspot> _hunkAreaHotspots = [];
    private GitDiffViewModel? _diffView;
    private IReadOnlyDictionary<string, GitDiffRowState>? _rowStatesByRowId;
    private GitDiffActionModel? _unstagedActions;
    private GitDiffActionModel? _stagedActions;
    private SharpIdeCodeEdit? _baseEditor;
    private SharpIdeCodeEdit? _currentEditor;
    private bool _isBusy;
    private bool _isDraggingDivider;
    private bool _isHoveringDivider;
    private string? _hoveredLineActionId;
    private string? _hoveredChunkActionId;
    private string? _hoveredChunkId;
    private float _lastDividerMouseX;
    private Vector2 _lastPointerPosition;
    private bool _hasLastPointerPosition;
    private bool _hasLastSnapshot;
    private GitDiffLayoutSnapshot _lastSnapshot;
    private int _stableFrames;
    private GitDiffTraceOperation? _pendingTraceOperation;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(MinimumGutterWidth, 420f);
    }

    public void BindEditors(SharpIdeCodeEdit baseEditor, SharpIdeCodeEdit currentEditor)
    {
        _baseEditor = baseEditor;
        _currentEditor = currentEditor;
        UpdateMinimumWidth();
        InvalidateLayout();
    }

    public void Configure(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId,
        GitDiffActionModel? unstagedActions,
        GitDiffActionModel? stagedActions)
    {
        _diffView = diffView;
        _rowStatesByRowId = rowStatesByRowId;
        _unstagedActions = unstagedActions;
        _stagedActions = stagedActions;
        _hoveredLineActionId = null;
        _hoveredChunkActionId = null;
        _hoveredChunkId = null;
        UpdateMinimumWidth();
        InvalidateLayout();
    }

    public void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        InvalidateLayout();
    }

    internal void SetPendingTraceOperation(GitDiffTraceOperation? traceOperation)
    {
        _pendingTraceOperation = traceOperation;
    }

    public void InvalidateLayout()
    {
        _stableFrames = 0;
        _hasLastSnapshot = false;
        if (!IsInsideTree())
        {
            return;
        }

        SetProcess(Visible && _diffView is not null && _baseEditor is not null && _currentEditor is not null);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var traceOperation = _pendingTraceOperation;
        using var redrawActivity = traceOperation?.StartChild($"{nameof(GitDiffActionGutter)}.Redraw");
        _lineMarkerHotspots.Clear();
        _chunkActionHotspots.Clear();
        _hunkAreaHotspots.Clear();
        if (_diffView is null || _baseEditor is null || _currentEditor is null)
        {
            return;
        }

        UpdateMinimumWidth();
        var metrics = new GitDiffLayoutMetrics(this, _baseEditor, _currentEditor, this);
        if (!metrics.TryGetActionGutterLayout(out var layout))
        {
            return;
        }

        using (traceOperation?.StartChild($"{nameof(GitDiffActionGutter)}.{nameof(DrawCanonicalBands)}"))
        {
            DrawCanonicalBands(metrics, layout);
        }

        DrawLineNumbers(metrics, GitDiffEditorSide.Left, layout.LeftLineNumberBounds, alignToRight: true);
        DrawLineNumbers(metrics, GitDiffEditorSide.Right, layout.RightLineNumberBounds, alignToRight: false);
        BuildChunkHoverHotspots(metrics);
        using (traceOperation?.StartChild($"{nameof(GitDiffActionGutter)}.{nameof(DrawChunkActions)}"))
        {
            DrawChunkActions(metrics, layout);
        }

        RefreshHoverFromLastPointer(includeLineMarkers: false);
        using (traceOperation?.StartChild($"{nameof(GitDiffActionGutter)}.{nameof(DrawLineActions)}"))
        {
            DrawLineActions(metrics, layout);
        }

        RefreshHoverFromLastPointer();
        DrawDivider(layout);
        if (traceOperation is not null)
        {
            _pendingTraceOperation = null;
            traceOperation.MarkRedrawCompleted(GitDiffTraceRedrawTarget.ActionGutter);
        }
    }

    public override void _Process(double delta)
    {
        if (_diffView is null || _baseEditor is null || _currentEditor is null)
        {
            SetProcess(false);
            return;
        }

        var metrics = new GitDiffLayoutMetrics(this, _baseEditor, _currentEditor, this);
        var snapshot = metrics.CaptureSnapshot();
        QueueRedraw();

        if (_hasLastSnapshot && snapshot.Equals(_lastSnapshot))
        {
            _stableFrames++;
        }
        else
        {
            _stableFrames = 0;
        }

        _lastSnapshot = snapshot;
        _hasLastSnapshot = true;
        if (_stableFrames >= 2)
        {
            SetProcess(false);
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_isBusy || _diffView is null)
        {
            if (_isBusy)
            {
                AcceptEvent();
            }

            base._GuiInput(@event);
            return;
        }

        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            _lastPointerPosition = mouseButton.Position;
            _hasLastPointerPosition = true;
            if (mouseButton.Pressed)
            {
                if (TryHitLineMarker(mouseButton.Position, out var lineAction))
                {
                    if (mouseButton.ShiftPressed)
                    {
                        InvokeChunkAction(lineAction.ToChunkAction());
                    }
                    else
                    {
                        InvokeLineAction(lineAction);
                    }

                    AcceptEvent();
                    return;
                }

                if (TryHitChunkAction(mouseButton.Position, out var chunkAction))
                {
                    InvokeChunkAction(chunkAction);
                    AcceptEvent();
                    return;
                }

                if (IsOnDivider(mouseButton.Position))
                {
                    _isDraggingDivider = true;
                    _lastDividerMouseX = mouseButton.GlobalPosition.X;
                    AcceptEvent();
                    return;
                }
            }
            else if (_isDraggingDivider)
            {
                _isDraggingDivider = false;
                AcceptEvent();
                return;
            }
        }

        if (@event is InputEventMouseMotion hoverMotion)
        {
            _lastPointerPosition = hoverMotion.Position;
            _hasLastPointerPosition = true;
            UpdateHoverState(hoverMotion.Position);
            var isHoveringDivider = IsOnDivider(hoverMotion.Position);
            if (_isHoveringDivider != isHoveringDivider)
            {
                _isHoveringDivider = isHoveringDivider;
                MouseDefaultCursorShape = isHoveringDivider ? CursorShape.Hsplit : CursorShape.Arrow;
                QueueRedraw();
            }
        }

        if (@event is InputEventMouseMotion dividerMotion && _isDraggingDivider)
        {
            var delta = dividerMotion.GlobalPosition.X - _lastDividerMouseX;
            _lastDividerMouseX = dividerMotion.GlobalPosition.X;
            DividerDragged?.Invoke(delta);
            AcceptEvent();
            return;
        }

        base._GuiInput(@event);
    }

    public override void _Input(InputEvent @event)
    {
        if (!_isDraggingDivider)
        {
            base._Input(@event);
            return;
        }

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            _isDraggingDivider = false;
            _isHoveringDivider = false;
            MouseDefaultCursorShape = CursorShape.Arrow;
            GetViewport().SetInputAsHandled();
        }

        base._Input(@event);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationMouseExit)
        {
            var hadHoverState =
                _hoveredLineActionId is not null ||
                _hoveredChunkActionId is not null ||
                _hoveredChunkId is not null ||
                _isHoveringDivider;
            _hasLastPointerPosition = false;
            _hoveredLineActionId = null;
            _hoveredChunkActionId = null;
            _hoveredChunkId = null;
            _isHoveringDivider = false;
            MouseDefaultCursorShape = CursorShape.Arrow;
            if (hadHoverState)
            {
                QueueRedraw();
            }
            return;
        }

        if (what == NotificationResized || what == NotificationThemeChanged)
        {
            InvalidateLayout();
        }
    }

    private void DrawCanonicalBands(GitDiffLayoutMetrics metrics, GitDiffActionGutterLayout layout)
    {
        if (_diffView is null)
        {
            return;
        }

        var baseLeftBandSpans = CollectBandSpans(metrics, layout, GitDiffEditorSide.Left, isStaged: false);
        var baseRightBandSpans = CollectBandSpans(metrics, layout, GitDiffEditorSide.Right, isStaged: false);
        foreach (var span in baseLeftBandSpans)
        {
            DrawGutterSpan(span);
        }

        foreach (var span in baseRightBandSpans)
        {
            DrawGutterSpan(span);
        }

        DrawOuterBorderMasks(baseLeftBandSpans, baseRightBandSpans);

        var stagedLeftBandSpans = CollectBandSpans(metrics, layout, GitDiffEditorSide.Left, isStaged: true, HasStagedTint);
        var stagedRightBandSpans = CollectBandSpans(metrics, layout, GitDiffEditorSide.Right, isStaged: true, HasStagedTint);
        foreach (var span in stagedLeftBandSpans)
        {
            DrawGutterSpan(span);
        }

        foreach (var span in stagedRightBandSpans)
        {
            DrawGutterSpan(span);
        }

        DrawOuterBorderMasks(stagedLeftBandSpans, stagedRightBandSpans);
    }

    private void DrawGutterSpan(GitDiffGutterSpan span)
    {
        if (span.IsInsertion)
        {
            DrawInsertionMarker(span.Rect, span.Kind, span.IsStaged);
            return;
        }

        DrawMarkRect(span.Rect, span.Kind, span.IsStaged);
    }

    private void DrawLineNumbers(GitDiffLayoutMetrics metrics, GitDiffEditorSide side, Rect2 columnBounds, bool alignToRight)
    {
        var editor = side is GitDiffEditorSide.Left ? _baseEditor : _currentEditor;
        if (editor is null)
        {
            return;
        }

        var font = editor.GetThemeFont(ThemeStringNames.Font);
        var fontSize = editor.GetThemeFontSize(ThemeStringNames.FontSize);
        var fontHeight = font.GetHeight(fontSize);
        var fontAscent = font.GetAscent(fontSize);
        foreach (var visibleLine in metrics.CollectVisibleEditorLines(side))
        {
            var rect = visibleLine.Rect;
            var top = rect.Position.Y;
            var baseline = top + Math.Max(0f, (rect.Size.Y - fontHeight) * 0.5f) + fontAscent;
            var text = visibleLine.LineNumber.ToString();
            var textWidth = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
            var x = alignToRight
                ? Math.Max(columnBounds.Position.X + 4f, columnBounds.End.X - 4f - textWidth)
                : columnBounds.Position.X + 4f;
            DrawString(font, new Vector2(x, baseline), text, HorizontalAlignment.Left, -1, fontSize, GitDiffPalette.SubtleTextColor);
        }
    }

    private void DrawLineActions(GitDiffLayoutMetrics metrics, GitDiffActionGutterLayout layout)
    {
        if (_diffView is null)
        {
            return;
        }

        var stagedByRowId = BuildLineActionLookup(_stagedActions?.LineActions);
        var unstagedByRowId = BuildLineActionLookup(_unstagedActions?.LineActions);

        foreach (var row in _diffView.Rows)
        {
            stagedByRowId.TryGetValue(row.RowId, out var stagedAction);
            unstagedByRowId.TryGetValue(row.RowId, out var unstagedAction);
            var rowStageState = GetRowStageState(row);
            var hasCheckedState = rowStageState is GitDiffRowStageState.Staged or GitDiffRowStageState.Mixed;
            if (stagedAction is null && unstagedAction is null && !hasCheckedState)
            {
                continue;
            }

            var actionType = stagedAction is not null || hasCheckedState
                ? LineActionType.Unstage
                : LineActionType.Stage;
            var isChecked = stagedAction is not null || hasCheckedState;
            var isVisibleBecauseHovered = IsHoveredChunkRow(row);
            if (!ShouldShowLineAction(isChecked, isVisibleBecauseHovered))
            {
                continue;
            }

            if (!metrics.TryGetCurrentSideActionMetrics(_diffView, row, GitDiffMetricMode.Primary, out var y, out var rowHeight))
            {
                continue;
            }

            var lineActionId = stagedAction?.LineActionId ?? unstagedAction?.LineActionId;
            var isHovered = lineActionId is not null &&
                            string.Equals(_hoveredLineActionId, lineActionId, StringComparison.Ordinal);
            var checkboxRect = GetActionControlRect(GitDiffEditorSide.Right, y, rowHeight, layout.StageColumnBounds);
            DrawCheckbox(checkboxRect, isChecked, isHovered);
            if (lineActionId is not null && (isChecked || isVisibleBecauseHovered))
            {
                _lineMarkerHotspots.Add(new LineActionHotspot(checkboxRect.Grow(4f), lineActionId, row.ChunkId, actionType));
            }
        }
    }

    private void BuildChunkHoverHotspots(GitDiffLayoutMetrics metrics)
    {
        if (_diffView is null)
        {
            return;
        }

        foreach (var chunk in _diffView.Chunks)
        {
            if (!metrics.TryGetChunkMetrics(_diffView, chunk, GitDiffMetricMode.Primary, out var top, out var height, out _))
            {
                continue;
            }

            _hunkAreaHotspots.Add(new HunkAreaHotspot(new Rect2(0f, top, Size.X, height), chunk.ChunkId));
        }
    }

    private void DrawChunkActions(GitDiffLayoutMetrics metrics, GitDiffActionGutterLayout layout)
    {
        if (_diffView is null)
        {
            return;
        }

        var rowsById = _diffView.Rows.ToDictionary(row => row.RowId, StringComparer.Ordinal);

        foreach (var chunkAction in _unstagedActions?.ChunkActions ?? [])
        {
            if (chunkAction.OperationMode is not GitPatchOperationMode.Revert)
            {
                continue;
            }

            DrawChunkAction(metrics, layout, rowsById, chunkAction);
        }
    }

    private void DrawChunkAction(
        GitDiffLayoutMetrics metrics,
        GitDiffActionGutterLayout layout,
        IReadOnlyDictionary<string, GitDiffDisplayRow> rowsById,
        GitDiffChunkActionAnchor chunkAction)
    {
        if (_diffView is null)
        {
            return;
        }

        if (!rowsById.TryGetValue(chunkAction.FirstCanonicalRowId, out var firstRow) ||
            !rowsById.TryGetValue(chunkAction.LastCanonicalRowId, out var lastRow))
        {
            return;
        }

        var preferredSide = chunkAction.OperationMode is GitPatchOperationMode.Revert
            ? GitDiffEditorSide.Left
            : GitDiffEditorSide.Right;
        if (!metrics.TryGetChunkActionMetrics(_diffView, firstRow, lastRow, GitDiffMetricMode.Primary, preferredSide, out var y, out var rowHeight))
        {
            return;
        }

        if (chunkAction.OperationMode is GitPatchOperationMode.Revert)
        {
            var revertRect = GetActionControlRect(GitDiffEditorSide.Left, y, rowHeight, layout.RevertColumnBounds);
            var isHovered = string.Equals(_hoveredChunkActionId, chunkAction.ActionChunkId, StringComparison.Ordinal);
            DrawRevertButton(revertRect, isHovered);
            _chunkActionHotspots.Add(new ChunkActionHotspot(revertRect.Grow(3f), chunkAction.ActionChunkId, ChunkActionType.Revert));
            return;
        }
    }

    private void DrawDivider(GitDiffActionGutterLayout layout)
    {
        var dividerColor = _isDraggingDivider || _isHoveringDivider
            ? GitDiffPalette.GutterDividerHoverColor
            : GitDiffPalette.GutterDividerColor;
        var dividerWidth = _isDraggingDivider || _isHoveringDivider ? 2f : 1f;
        DrawLine(new Vector2(layout.DividerX, 0f), new Vector2(layout.DividerX, Size.Y), dividerColor, dividerWidth);
    }

    private bool HasStagedTint(IReadOnlyList<GitDiffDisplayRow> rows)
    {
        return rows.Any(HasStagedTint);
    }

    private bool HasStagedTint(GitDiffDisplayRow row)
    {
        return GetRowStageState(row) is GitDiffRowStageState.Staged or GitDiffRowStageState.Mixed;
    }

    private GitDiffRowStageState? GetRowStageState(GitDiffDisplayRow row)
    {
        if (_rowStatesByRowId is null ||
            !_rowStatesByRowId.TryGetValue(row.RowId, out var rowState))
        {
            return null;
        }

        return rowState.StageState;
    }

    private bool IsHoveredChunkRow(GitDiffDisplayRow row)
    {
        return _hoveredChunkId is not null &&
               string.Equals(_hoveredChunkId, row.ChunkId, StringComparison.Ordinal);
    }

    private IReadOnlyList<GitDiffGutterSpan> CollectBandSpans(
        GitDiffLayoutMetrics metrics,
        GitDiffActionGutterLayout layout,
        GitDiffEditorSide side,
        bool isStaged,
        Func<GitDiffDisplayRow, bool>? includeRow = null)
    {
        if (_diffView is null)
        {
            return [];
        }

        var spans = new List<GitDiffGutterSpan>();
        foreach (var segment in GitDiffLayoutMetrics.BuildVisualSegments(_diffView, includeRow))
        {
            spans.AddRange(metrics.CollectVisibleGutterSpans(_diffView, segment, GitDiffMetricMode.Primary, layout, side, isStaged));
        }

        return [.. GitDiffLayoutMetrics.MergeGutterSpans(spans)];
    }

    private bool ShouldShowLineAction(bool isChecked, bool isVisibleBecauseHovered)
    {
        return isChecked || isVisibleBecauseHovered;
    }

    private void UpdateHoverState(Vector2 position, bool includeLineMarkers = true)
    {
        var hoveredLineHotspot = includeLineMarkers && TryHitLineMarker(position, out var lineHotspot)
            ? lineHotspot
            : default(LineActionHotspot?);
        var hoveredChunkHotspot = TryHitChunkAction(position, out var chunkHotspot)
            ? chunkHotspot
            : default(ChunkActionHotspot?);
        var hoveredLineActionId = hoveredLineHotspot?.LineActionId;
        var hoveredChunkId = TryHitHunkArea(position, out var hunkAreaHotspot)
            ? hunkAreaHotspot.ChunkId
            : null;
        var hoveredChunkActionId = hoveredChunkHotspot?.ChunkId;

        var changed = !string.Equals(_hoveredLineActionId, hoveredLineActionId, StringComparison.Ordinal) ||
                      !string.Equals(_hoveredChunkActionId, hoveredChunkActionId, StringComparison.Ordinal) ||
                      !string.Equals(_hoveredChunkId, hoveredChunkId, StringComparison.Ordinal);

        _hoveredLineActionId = hoveredLineActionId;
        _hoveredChunkActionId = hoveredChunkActionId;
        _hoveredChunkId = hoveredChunkId;

        if (changed)
        {
            QueueRedraw();
        }
    }

    private void RefreshHoverFromLastPointer(bool includeLineMarkers = true)
    {
        if (!_hasLastPointerPosition)
        {
            return;
        }

        if (_lastPointerPosition.X < 0f || _lastPointerPosition.Y < 0f || _lastPointerPosition.X > Size.X || _lastPointerPosition.Y > Size.Y)
        {
            return;
        }

        UpdateHoverState(_lastPointerPosition, includeLineMarkers);
    }

    private Rect2 GetActionControlRect(GitDiffEditorSide side, float y, float rowHeight, Rect2 columnBounds)
    {
        return GetCenteredRect(side, y, rowHeight, columnBounds, new Vector2(ActionControlSize, ActionControlSize));
    }

    private Rect2 GetCenteredRect(GitDiffEditorSide side, float y, float rowHeight, Rect2 columnBounds, Vector2 size)
    {
        var x = columnBounds.GetCenter().X < (Size.X * 0.5f)
            ? Mathf.Round(columnBounds.End.X - size.X - ActionControlInnerPadding)
            : Mathf.Round(columnBounds.Position.X + ActionControlInnerPadding);
        var editor = side is GitDiffEditorSide.Left ? _baseEditor : _currentEditor;
        var top = y + Mathf.Max(0f, (rowHeight - size.Y) * 0.5f);
        if (editor is not null)
        {
            var font = editor.GetThemeFont(ThemeStringNames.Font);
            var fontSize = editor.GetThemeFontSize(ThemeStringNames.FontSize);
            var fontHeight = font.GetHeight(fontSize);
            var textTop = y + Math.Max(0f, (rowHeight - fontHeight) * 0.5f);
            top = textTop + Math.Max(0f, (fontHeight - size.Y) * 0.5f);
        }

        top = Mathf.Round(top + ActionControlVerticalOffset);
        return new Rect2(new Vector2(x, top), size);
    }

    private void DrawMarkRect(Rect2 rect, GitDiffChunkBackgroundKind kind, bool isStaged)
    {
        DrawRect(rect, GitDiffPalette.GetGutterGlowColor(kind, isStaged));
        DrawRect(rect, GitDiffPalette.GetGutterFillColor(kind, isStaged));
        if (rect.Size.Y < 1f)
        {
            return;
        }

        var leftX = rect.Position.X;
        var rightX = rect.End.X;
        var topY = rect.Position.Y + 0.5f;
        var bottomY = rect.End.Y - 0.5f;
        DrawPolyline([new Vector2(leftX, topY), new Vector2(rightX, topY)], GitDiffPalette.GetGutterStrokeColor(kind, isStaged, 0.28f), 1f, antialiased: true);
        DrawPolyline([new Vector2(leftX, bottomY), new Vector2(rightX, bottomY)], GitDiffPalette.GetGutterStrokeColor(kind, isStaged, 0.28f), 1f, antialiased: true);
    }

    private void DrawInsertionMarker(Rect2 rect, GitDiffChunkBackgroundKind kind, bool isStaged)
    {
        DrawRect(rect, GitDiffPalette.GetGutterBandColor(kind, isStaged));
        DrawLine(new Vector2(rect.Position.X, rect.GetCenter().Y), new Vector2(rect.End.X, rect.GetCenter().Y), GitDiffPalette.GetGutterStrokeColor(kind, isStaged, 0.28f), 1f);
    }

    private void DrawOuterBorderMasks(IReadOnlyList<GitDiffGutterSpan> leftBandSpans, IReadOnlyList<GitDiffGutterSpan> rightBandSpans)
    {
        foreach (var span in leftBandSpans)
        {
            DrawRect(new Rect2(new Vector2(0f, span.Rect.Position.Y), new Vector2(1f, span.Rect.Size.Y)), GitDiffPalette.GetGutterBandColor(span.Kind, span.IsStaged));
        }

        var rightBorderX = Math.Max(0f, Size.X - 1f);
        foreach (var span in rightBandSpans)
        {
            DrawRect(new Rect2(new Vector2(rightBorderX, span.Rect.Position.Y), new Vector2(1f, span.Rect.Size.Y)), GitDiffPalette.GetGutterBandColor(span.Kind, span.IsStaged));
        }
    }

    private void DrawCheckbox(Rect2 rect, bool isChecked, bool isHovered)
    {
        DrawRect(rect, isHovered ? new Color("2a3039") : new Color("20252d"));
        DrawRect(rect, isHovered ? new Color("b3bed0") : new Color("93a0b5"), false, 1f);
        if (!isChecked)
        {
            return;
        }

        DrawLine(rect.Position + new Vector2(2.5f, rect.Size.Y * 0.55f), rect.Position + new Vector2(rect.Size.X * 0.42f, rect.Size.Y - 2.5f), Colors.White, 1.5f);
        DrawLine(rect.Position + new Vector2(rect.Size.X * 0.42f, rect.Size.Y - 2.5f), rect.End - new Vector2(2.5f, rect.Size.Y - 3.5f), Colors.White, 1.5f);
    }

    private void DrawRevertButton(Rect2 rect, bool isHovered)
    {
        var iconRect = rect.GrowIndividual(-0.5f, -0.5f, -0.5f, -0.5f);
        if (isHovered)
        {
            iconRect = iconRect.Grow(0.5f);
        }

        DrawTextureRect(RevertChunkChevronIcon, iconRect, false);
    }

    private bool TryHitLineMarker(Vector2 position, out LineActionHotspot hotspot)
    {
        for (var index = _lineMarkerHotspots.Count - 1; index >= 0; index--)
        {
            var candidate = _lineMarkerHotspots[index];
            if (!candidate.Rect.HasPoint(position))
            {
                continue;
            }

            hotspot = candidate;
            return true;
        }

        hotspot = default;
        return false;
    }

    private bool TryHitHunkArea(Vector2 position, out HunkAreaHotspot hotspot)
    {
        for (var index = _hunkAreaHotspots.Count - 1; index >= 0; index--)
        {
            var candidate = _hunkAreaHotspots[index];
            if (!candidate.Rect.HasPoint(position))
            {
                continue;
            }

            hotspot = candidate;
            return true;
        }

        hotspot = default;
        return false;
    }

    private bool TryHitChunkAction(Vector2 position, out ChunkActionHotspot hotspot)
    {
        for (var index = _chunkActionHotspots.Count - 1; index >= 0; index--)
        {
            var candidate = _chunkActionHotspots[index];
            if (!candidate.Rect.HasPoint(position))
            {
                continue;
            }

            hotspot = candidate;
            return true;
        }

        hotspot = default;
        return false;
    }

    private bool IsOnDivider(Vector2 position)
    {
        var layout = GitDiffActionGutterLayout.Create(new Rect2(Vector2.Zero, Size));
        return layout.DividerHitBounds.HasPoint(position);
    }

    private void UpdateMinimumWidth()
    {
        var numberColumnWidth = Math.Max(GetRequiredNumberColumnWidth(_baseEditor), GetRequiredNumberColumnWidth(_currentEditor));
        var targetWidth = MathF.Max(
            MinimumGutterWidth,
            (numberColumnWidth * 2f) +
            GitDiffActionGutterLayout.RevertColumnWidth +
            GitDiffActionGutterLayout.MiddleBandWidth +
            GitDiffActionGutterLayout.StageColumnWidth);
        if (Mathf.IsEqualApprox(CustomMinimumSize.X, targetWidth))
        {
            return;
        }

        CustomMinimumSize = new Vector2(targetWidth, CustomMinimumSize.Y);
        if (GetParent() is Container container)
        {
            container.QueueSort();
        }
    }

    private float GetRequiredNumberColumnWidth(SharpIdeCodeEdit? editor)
    {
        if (editor is null)
        {
            return 24f;
        }

        var font = editor.GetThemeFont(ThemeStringNames.Font);
        var fontSize = editor.GetThemeFontSize(ThemeStringNames.FontSize);
        var lineCount = Math.Max(1, editor.GetLineCount());
        return MathF.Ceiling(font.GetStringSize(lineCount.ToString(), HorizontalAlignment.Left, -1, fontSize).X + NumberColumnPadding);
    }

    private static IReadOnlyDictionary<string, GitDiffLineActionAnchor> BuildLineActionLookup(IReadOnlyList<GitDiffLineActionAnchor>? actions)
    {
        if (actions is null || actions.Count is 0)
        {
            return new Dictionary<string, GitDiffLineActionAnchor>(StringComparer.Ordinal);
        }

        var byRowId = new Dictionary<string, GitDiffLineActionAnchor>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (byRowId.ContainsKey(action.CanonicalRowId))
            {
                continue;
            }

            byRowId[action.CanonicalRowId] = action;
        }

        return byRowId;
    }

    private void InvokeLineAction(LineActionHotspot hotspot)
    {
        var ids = new[] { hotspot.LineActionId };
        if (hotspot.ActionType is LineActionType.Unstage)
        {
            UnstageLinesRequested?.Invoke(ids);
            return;
        }

        StageLinesRequested?.Invoke(ids);
    }

    private void InvokeChunkAction(ChunkActionHotspot hotspot)
    {
        switch (hotspot.ActionType)
        {
            case ChunkActionType.Stage:
                StageChunkRequested?.Invoke(hotspot.ChunkId);
                break;
            case ChunkActionType.Unstage:
                UnstageChunkRequested?.Invoke(hotspot.ChunkId);
                break;
            case ChunkActionType.Revert:
                RevertChunkRequested?.Invoke(hotspot.ChunkId);
                break;
        }
    }

    private readonly record struct LineActionHotspot(Rect2 Rect, string LineActionId, string ChunkId, LineActionType ActionType)
    {
        public ChunkActionHotspot ToChunkAction()
        {
            return new ChunkActionHotspot(
                Rect,
                ChunkId,
                ActionType is LineActionType.Unstage ? ChunkActionType.Unstage : ChunkActionType.Stage);
        }
    }

    private readonly record struct ChunkActionHotspot(Rect2 Rect, string ChunkId, ChunkActionType ActionType);
    private readonly record struct HunkAreaHotspot(Rect2 Rect, string ChunkId);

    private enum LineActionType
    {
        Stage,
        Unstage
    }

    private enum ChunkActionType
    {
        Stage,
        Unstage,
        Revert
    }
}
