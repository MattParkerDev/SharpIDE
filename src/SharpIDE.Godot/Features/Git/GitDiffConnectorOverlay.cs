using Godot;
using SharpIDE.Application.Features.Git;
using SharpIDE.Godot.Features.CodeEditor;

namespace SharpIDE.Godot.Features.Git;

public partial class GitDiffConnectorOverlay : Control
{
    private const int CurveSamples = 24;
    private const float MissingSideConnectorHeight = 2f;

    private Control? _diffViewportHost;
    private HBoxContainer? _diffEditorRow;
    private Control? _actionGutter;
    private SharpIdeCodeEdit? _baseEditor;
    private SharpIdeCodeEdit? _currentEditor;
    private GitDiffViewModel? _diffView;
    private IReadOnlyDictionary<string, GitDiffRowState>? _rowStatesByRowId;
    private bool _hasLastSnapshot;
    private GitDiffLayoutSnapshot _lastSnapshot;
    private int _stableFrames;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ZAsRelative = false;
        ZIndex = 100;
    }

    public void BindLayout(Control diffViewportHost, HBoxContainer diffEditorRow, Control actionGutter)
    {
        var previousViewportHost = _diffViewportHost;
        if (!ReferenceEquals(_diffViewportHost, diffViewportHost))
        {
            if (previousViewportHost?.IsAlive() == true)
            {
                previousViewportHost!.ItemRectChanged -= OnLayoutChanged;
            }

            _diffViewportHost = diffViewportHost;
            _diffViewportHost.ItemRectChanged += OnLayoutChanged;
        }

        var previousEditorRow = _diffEditorRow;
        if (!ReferenceEquals(_diffEditorRow, diffEditorRow))
        {
            if (previousEditorRow?.IsAlive() == true)
            {
                previousEditorRow!.ItemRectChanged -= OnLayoutChanged;
            }

            _diffEditorRow = diffEditorRow;
            _diffEditorRow.ItemRectChanged += OnLayoutChanged;
        }

        var previousActionGutter = _actionGutter;
        if (!ReferenceEquals(_actionGutter, actionGutter))
        {
            if (previousActionGutter?.IsAlive() == true)
            {
                previousActionGutter!.ItemRectChanged -= OnLayoutChanged;
            }

            _actionGutter = actionGutter;
            _actionGutter.ItemRectChanged += OnLayoutChanged;
        }

        InvalidateLayout();
    }

    public void BindEditors(SharpIdeCodeEdit baseEditor, SharpIdeCodeEdit currentEditor)
    {
        var previousBaseEditor = _baseEditor;
        if (!ReferenceEquals(_baseEditor, baseEditor))
        {
            if (previousBaseEditor?.IsAlive() == true)
            {
                previousBaseEditor!.ItemRectChanged -= OnLayoutChanged;
            }

            _baseEditor = baseEditor;
            _baseEditor.ItemRectChanged += OnLayoutChanged;
        }

        var previousCurrentEditor = _currentEditor;
        if (!ReferenceEquals(_currentEditor, currentEditor))
        {
            if (previousCurrentEditor?.IsAlive() == true)
            {
                previousCurrentEditor!.ItemRectChanged -= OnLayoutChanged;
            }

            _currentEditor = currentEditor;
            _currentEditor.ItemRectChanged += OnLayoutChanged;
        }

        InvalidateLayout();
    }

    public void ClearBindings()
    {
        var baseEditor = _baseEditor;
        if (baseEditor?.IsAlive() == true)
        {
            baseEditor!.ItemRectChanged -= OnLayoutChanged;
        }

        var currentEditor = _currentEditor;
        if (currentEditor?.IsAlive() == true)
        {
            currentEditor!.ItemRectChanged -= OnLayoutChanged;
        }

        _baseEditor = null;
        _currentEditor = null;
        _diffView = null;
        _rowStatesByRowId = null;
        _hasLastSnapshot = false;
        _stableFrames = 0;
        SetProcess(false);
        QueueRedraw();
    }

    public void Configure(GitDiffViewModel? diffView, IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId = null)
    {
        _diffView = diffView;
        _rowStatesByRowId = rowStatesByRowId;
        Visible = diffView is not null;
        InvalidateLayout();
    }

    public void InvalidateLayout()
    {
        _stableFrames = 0;
        _hasLastSnapshot = false;
        if (!IsInsideTree())
        {
            return;
        }

        SetProcess(Visible && _diffView is not null);
        QueueRedraw();
    }

    public override void _ExitTree()
    {
        var diffViewportHost = _diffViewportHost;
        if (diffViewportHost?.IsAlive() == true)
        {
            diffViewportHost!.ItemRectChanged -= OnLayoutChanged;
        }

        var diffEditorRow = _diffEditorRow;
        if (diffEditorRow?.IsAlive() == true)
        {
            diffEditorRow!.ItemRectChanged -= OnLayoutChanged;
        }

        var actionGutter = _actionGutter;
        if (actionGutter?.IsAlive() == true)
        {
            actionGutter!.ItemRectChanged -= OnLayoutChanged;
        }

        var baseEditor = _baseEditor;
        if (baseEditor?.IsAlive() == true)
        {
            baseEditor!.ItemRectChanged -= OnLayoutChanged;
        }

        var currentEditor = _currentEditor;
        if (currentEditor?.IsAlive() == true)
        {
            currentEditor!.ItemRectChanged -= OnLayoutChanged;
        }
    }

    public override void _Process(double delta)
    {
        if (_diffView is null || _baseEditor?.IsAlive() != true || _currentEditor?.IsAlive() != true || _actionGutter?.IsAlive() != true)
        {
            SetProcess(false);
            return;
        }

        var metrics = new GitDiffLayoutMetrics(this, _baseEditor, _currentEditor, _actionGutter);
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

    public override void _Draw()
    {
        if (_diffView is null || _baseEditor?.IsAlive() != true || _currentEditor?.IsAlive() != true || _actionGutter?.IsAlive() != true)
        {
            return;
        }

        var metrics = new GitDiffLayoutMetrics(this, _baseEditor, _currentEditor, _actionGutter);
        DrawSegments(metrics, _diffView, GitDiffLayoutMetrics.BuildVisualSegments(_diffView), isStagedOverlay: false);
        DrawSegments(metrics, _diffView, GitDiffLayoutMetrics.BuildVisualSegments(_diffView, IsStagedRow), isStagedOverlay: true);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationResized || what == NotificationThemeChanged)
        {
            InvalidateLayout();
        }
    }

    private void DrawSegments(
        GitDiffLayoutMetrics metrics,
        GitDiffViewModel diffView,
        IReadOnlyList<GitDiffVisualSegment> segments,
        bool isStagedOverlay)
    {
        var mode = isStagedOverlay ? GitDiffMetricMode.StagedOverlay : GitDiffMetricMode.Primary;
        foreach (var segment in segments)
        {
            DrawFullWidthInsertionMarker(metrics, diffView, segment, mode, isStagedOverlay);
            if (!TryCreateRibbon(metrics, diffView, segment, isStagedOverlay, out var fillPolygon, out var topCurve, out var bottomCurve, out var kind))
            {
                continue;
            }

            DrawColoredPolygon(fillPolygon, GitDiffPalette.GetConnectorGlowColor(kind, isStagedOverlay));
            DrawColoredPolygon(fillPolygon, GitDiffPalette.GetConnectorFillColor(kind, isStagedOverlay));
            DrawPolyline(InsetCurve(topCurve, 0.5f), GitDiffPalette.GetConnectorStrokeColor(kind, isStagedOverlay, 0.28f), 1f, antialiased: true);
            DrawPolyline(InsetCurve(bottomCurve, -0.5f), GitDiffPalette.GetConnectorStrokeColor(kind, isStagedOverlay, 0.28f), 1f, antialiased: true);
        }
    }

    private bool IsStagedRow(GitDiffDisplayRow row)
    {
        if (_rowStatesByRowId is null)
        {
            return false;
        }

        return _rowStatesByRowId.TryGetValue(row.RowId, out var rowState) &&
               rowState.StageState is GitDiffRowStageState.Staged or GitDiffRowStageState.Mixed;
    }

    private void DrawFullWidthInsertionMarker(
        GitDiffLayoutMetrics metrics,
        GitDiffViewModel diffView,
        GitDiffVisualSegment segment,
        GitDiffMetricMode mode,
        bool isStaged)
    {
        if (!metrics.TryGetVisibleSegmentGeometry(segment, mode, out var geometry) ||
            geometry.HasLeft == geometry.HasRight)
        {
            return;
        }

        var missingSide = geometry.HasLeft ? GitDiffEditorSide.Right : GitDiffEditorSide.Left;
        if (!metrics.TryGetEditorBounds(missingSide, out var markerBounds) ||
            !metrics.TryGetInsertionMarkerRect(diffView, segment, mode, missingSide, markerBounds, out var markerRect))
        {
            return;
        }

        DrawInsertionMarker(markerRect, segment.BackgroundKind, isStaged);
    }

    private bool TryCreateRibbon(
        GitDiffLayoutMetrics metrics,
        GitDiffViewModel diffView,
        GitDiffVisualSegment segment,
        bool overlayOnly,
        out Vector2[] fillPolygon,
        out Vector2[] topCurve,
        out Vector2[] bottomCurve,
        out GitDiffChunkBackgroundKind backgroundKind)
    {
        fillPolygon = [];
        topCurve = [];
        bottomCurve = [];
        backgroundKind = segment.BackgroundKind;

        if (!metrics.TryGetActionGutterLayout(out var gutterLayout) ||
            !metrics.TryGetVisibleSegmentGeometry(segment, overlayOnly ? GitDiffMetricMode.StagedOverlay : GitDiffMetricMode.Primary, out var geometry))
        {
            return false;
        }

        var mode = overlayOnly ? GitDiffMetricMode.StagedOverlay : GitDiffMetricMode.Primary;
        var middleBandLeftX = gutterLayout.LeftBandInnerX;
        var middleBandRightX = gutterLayout.RightBandInnerX;

        if (geometry.HasLeft && !geometry.HasRight)
        {
            var startTop = geometry.LeftTop.GetValueOrDefault();
            var startBottom = geometry.LeftBottom.GetValueOrDefault(startTop);
            var targetHeight = MissingSideConnectorHeight;
            var endCenterY = (startTop + startBottom) * 0.5f;
            if (metrics.TryGetInsertionMarkerRect(diffView, segment, mode, GitDiffEditorSide.Right, gutterLayout.RightBandBounds, out var marker))
            {
                targetHeight = Math.Max(MissingSideConnectorHeight, marker.Size.Y);
                endCenterY = marker.GetCenter().Y;
            }

            BuildRibbon(
                startX: middleBandLeftX,
                startTop: startTop,
                startBottom: startBottom,
                endX: middleBandRightX,
                endTop: endCenterY - (targetHeight * 0.5f),
                endBottom: endCenterY + (targetHeight * 0.5f),
                controlStartX: gutterLayout.LeftConnectorEntryX,
                controlEndX: gutterLayout.RightConnectorEntryX,
                out fillPolygon,
                out topCurve,
                out bottomCurve);
            return true;
        }

        if (!geometry.HasLeft && geometry.HasRight)
        {
            var endTop = geometry.RightTop.GetValueOrDefault();
            var endBottom = geometry.RightBottom.GetValueOrDefault(endTop);
            var targetHeight = MissingSideConnectorHeight;
            var startCenterY = (endTop + endBottom) * 0.5f;
            if (metrics.TryGetInsertionMarkerRect(diffView, segment, mode, GitDiffEditorSide.Left, gutterLayout.LeftBandBounds, out var marker))
            {
                targetHeight = Math.Max(MissingSideConnectorHeight, marker.Size.Y);
                startCenterY = marker.GetCenter().Y;
            }

            BuildRibbon(
                startX: middleBandLeftX,
                startTop: startCenterY - (targetHeight * 0.5f),
                startBottom: startCenterY + (targetHeight * 0.5f),
                endX: middleBandRightX,
                endTop: endTop,
                endBottom: endBottom,
                controlStartX: gutterLayout.LeftConnectorEntryX,
                controlEndX: gutterLayout.RightConnectorEntryX,
                out fillPolygon,
                out topCurve,
                out bottomCurve);
            return true;
        }

        BuildRibbon(
            startX: middleBandLeftX,
            startTop: geometry.LeftTop!.Value,
            startBottom: geometry.LeftBottom!.Value,
            endX: middleBandRightX,
            endTop: geometry.RightTop!.Value,
            endBottom: geometry.RightBottom!.Value,
            controlStartX: gutterLayout.LeftConnectorEntryX,
            controlEndX: gutterLayout.RightConnectorEntryX,
            out fillPolygon,
            out topCurve,
            out bottomCurve);
        return true;
    }

    private static void BuildRibbon(
        float startX,
        float startTop,
        float startBottom,
        float endX,
        float endTop,
        float endBottom,
        float controlStartX,
        float controlEndX,
        out Vector2[] fillPolygon,
        out Vector2[] topCurve,
        out Vector2[] bottomCurve)
    {
        topCurve = SampleCurve(
            new Vector2(startX, startTop),
            new Vector2(controlStartX, startTop),
            new Vector2(controlEndX, endTop),
            new Vector2(endX, endTop));
        bottomCurve = SampleCurve(
            new Vector2(endX, endBottom),
            new Vector2(controlEndX, endBottom),
            new Vector2(controlStartX, startBottom),
            new Vector2(startX, startBottom));

        fillPolygon = new Vector2[topCurve.Length + bottomCurve.Length];
        Array.Copy(topCurve, fillPolygon, topCurve.Length);
        Array.Copy(bottomCurve, 0, fillPolygon, topCurve.Length, bottomCurve.Length);
    }

    private static Vector2[] SampleCurve(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end)
    {
        var points = new Vector2[CurveSamples + 1];
        for (var i = 0; i <= CurveSamples; i++)
        {
            var t = i / (float)CurveSamples;
            points[i] = EvaluateCubicBezier(start, control1, control2, end, t);
        }

        return points;
    }

    private static Vector2[] InsetCurve(Vector2[] curve, float yOffset)
    {
        var insetCurve = new Vector2[curve.Length];
        for (var i = 0; i < curve.Length; i++)
        {
            insetCurve[i] = new Vector2(curve[i].X, curve[i].Y + yOffset);
        }

        return insetCurve;
    }

    private void DrawInsertionMarker(Rect2 rect, GitDiffChunkBackgroundKind kind, bool isStaged)
    {
        DrawRect(rect, GitDiffPalette.GetChunkBandColor(kind, isStaged));
        DrawLine(
            new Vector2(rect.Position.X, rect.GetCenter().Y),
            new Vector2(rect.End.X, rect.GetCenter().Y),
            GitDiffPalette.GetConnectorStrokeColor(kind, isStaged),
            1f);
    }

    private static Vector2 EvaluateCubicBezier(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end, float t)
    {
        var u = 1f - t;
        return (u * u * u * start) +
               (3f * u * u * t * control1) +
               (3f * u * t * t * control2) +
               (t * t * t * end);
    }

    private void OnLayoutChanged()
    {
        InvalidateLayout();
    }
}
