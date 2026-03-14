using Godot;
using SharpIDE.Application.Features.Git;
using SharpIDE.Godot.Features.Git;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class GitChangeScrollbarOverlay : Control
{
    private const float MinimumThumbLength = 18f;
    private const double ScrollAnimationDurationSeconds = 0.07d;

    public enum MarkerHorizontalAlignment
    {
        Left,
        Right
    }

    private IReadOnlyList<GitDiffScrollMarker> _markers = [];
    private VScrollBar? _scrollBar;
    private Control? _layoutHost;
    private Func<int>? _getLineCount;
    private Action<int>? _navigateToLine;
    private MarkerHorizontalAlignment _horizontalAlignment = MarkerHorizontalAlignment.Right;
    private bool _scrollBarLayoutBound;
    private bool _layoutHostBound;
    private bool _isDraggingThumb;
    private float _thumbDragPointerOffsetY;
    private Tween? _scrollTween;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        Visible = false;
    }

    public void Bind(SharpIdeCodeEdit editor)
    {
        Bind(editor.GetVScrollBar(), editor.GetLineCount, editor.NavigateToGitChange, MarkerHorizontalAlignment.Right);
    }

    public void Bind(VScrollBar scrollBar, Func<int> getLineCount, Action<int> navigateToLine, MarkerHorizontalAlignment horizontalAlignment)
    {
        UnbindTrackedLayout();
        _scrollBar = scrollBar;
        _layoutHost = GetParent() as Control;
        _getLineCount = getLineCount;
        _navigateToLine = navigateToLine;
        _horizontalAlignment = horizontalAlignment;
        BindTrackedLayout();
        RefreshLayout();
    }

    public void SetMarkers(IReadOnlyList<GitDiffScrollMarker> markers)
    {
        _markers = markers;
        Visible = markers.Count > 0;
        RefreshLayout();
        QueueRedraw();
    }

    public void ClearMarkers()
    {
        if (_markers.Count is 0 && Visible is false) return;

        _markers = [];
        Visible = false;
        QueueRedraw();
    }

    public void RefreshLayout()
    {
        if (_scrollBar is null) return;

        Position = GetParent() == _scrollBar ? Vector2.Zero : _scrollBar.Position;
        Size = _scrollBar.Size;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_scrollBar is null)
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown or MouseButton.WheelUp } wheelEvent)
        {
            var direction = wheelEvent.ButtonIndex is MouseButton.WheelDown ? 1d : -1d;
            AnimateScrollBarToValue(_scrollBar.Value + (Math.Max(1d, _scrollBar.Step) * direction), snapToStep: true);
            AcceptEvent();
            return;
        }

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseEvent)
        {
            if (mouseEvent.Pressed)
            {
                var thumbRect = GetThumbRect();
                if (thumbRect.HasPoint(mouseEvent.Position))
                {
                    StopScrollTween();
                    _isDraggingThumb = true;
                    _thumbDragPointerOffsetY = mouseEvent.Position.Y - thumbRect.Position.Y;
                    AcceptEvent();
                    return;
                }

                var marker = GetMarkerAtPosition(mouseEvent.Position);
                if (marker is not null)
                {
                    _navigateToLine?.Invoke(marker.TargetLine);
                    AcceptEvent();
                    return;
                }

                AnimateScrollBarToPointer(mouseEvent.Position.Y, centerThumbOnPointer: true, snapToStep: true);
                AcceptEvent();
                return;
            }

            if (_isDraggingThumb)
            {
                EndThumbDrag();
                AcceptEvent();
                return;
            }
        }

        if (@event is InputEventMouseMotion mouseMotion && _isDraggingThumb)
        {
            SetScrollBarValueFromPointer(mouseMotion.Position.Y, centerThumbOnPointer: false);
            AcceptEvent();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!_isDraggingThumb)
        {
            base._Input(@event);
            return;
        }

        if (@event is InputEventMouseMotion)
        {
            SetScrollBarValueFromPointer(GetLocalMousePosition().Y, centerThumbOnPointer: false);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            EndThumbDrag();
            GetViewport().SetInputAsHandled();
        }

        base._Input(@event);
    }

    public override void _Draw()
    {
        if (_getLineCount is null || _markers.Count is 0) return;

        var totalLines = Math.Max(_getLineCount(), 1);
        foreach (var marker in _markers)
        {
            DrawRect(GetMarkerRect(marker, totalLines), GetMarkerColor(marker.Kind));
        }
    }

    private GitDiffScrollMarker? GetMarkerAtPosition(Vector2 position)
    {
        if (_getLineCount is null || _markers.Count is 0)
        {
            return null;
        }

        var totalLines = Math.Max(_getLineCount(), 1);
        foreach (var marker in _markers)
        {
            if (GetMarkerRect(marker, totalLines).HasPoint(position))
            {
                return marker;
            }
        }

        return null;
    }

    private Rect2 GetMarkerRect(GitDiffScrollMarker marker, int totalLines)
    {
        var paddingX = MathF.Min(2f, Size.X * 0.2f);
        var trackWidth = MathF.Max(2f, Size.X - (paddingX * 2f));
        var normalizedStart = Mathf.Clamp((float)marker.AnchorLine / totalLines, 0f, 1f);
        var normalizedHeight = Mathf.Clamp((float)Math.Max(marker.LineSpan, 1) / totalLines, 0f, 1f);
        var height = MathF.Max(3f, Size.Y * normalizedHeight);
        var y = (Size.Y - height) * normalizedStart;
        var x = _horizontalAlignment is MarkerHorizontalAlignment.Right
            ? Size.X - trackWidth - paddingX
            : paddingX;

        return new Rect2(new Vector2(x, y), new Vector2(trackWidth, height));
    }

    private static Color GetMarkerColor(GitDiffScrollMarkerKind kind)
    {
        return kind switch
        {
            GitDiffScrollMarkerKind.Added => GitDiffPalette.GetRowMarkerColor(GitDiffDisplayRowKind.Added),
            GitDiffScrollMarkerKind.Removed => GitDiffPalette.GetRowMarkerColor(GitDiffDisplayRowKind.Removed),
            _ => GitDiffPalette.GetRowMarkerColor(GitDiffDisplayRowKind.ModifiedRight)
        };
    }

    public override void _ExitTree()
    {
        StopScrollTween();
        UnbindTrackedLayout();
        base._ExitTree();
    }

    private void BindTrackedLayout()
    {
        if (_scrollBar is not null)
        {
            _scrollBar.ItemRectChanged += OnTrackedLayoutChanged;
            _scrollBar.Resized += OnTrackedLayoutChanged;
            _scrollBarLayoutBound = true;
        }

        if (_layoutHost is not null && !ReferenceEquals(_layoutHost, _scrollBar))
        {
            _layoutHost.ItemRectChanged += OnTrackedLayoutChanged;
            _layoutHost.Resized += OnTrackedLayoutChanged;
            _layoutHostBound = true;
        }
    }

    private void UnbindTrackedLayout()
    {
        if (_scrollBarLayoutBound && _scrollBar is not null)
        {
            _scrollBar.ItemRectChanged -= OnTrackedLayoutChanged;
            _scrollBar.Resized -= OnTrackedLayoutChanged;
            _scrollBarLayoutBound = false;
        }

        if (_layoutHostBound && _layoutHost is not null)
        {
            _layoutHost.ItemRectChanged -= OnTrackedLayoutChanged;
            _layoutHost.Resized -= OnTrackedLayoutChanged;
            _layoutHostBound = false;
        }
    }

    private void OnTrackedLayoutChanged()
    {
        CallDeferred(MethodName.RefreshLayout);
    }

    private Rect2 GetThumbRect()
    {
        if (_scrollBar is null || Size.Y <= 0f || Size.X <= 0f)
        {
            return new Rect2(Vector2.Zero, Size);
        }

        var trackLength = Size.Y;
        var totalRange = Math.Max(0d, _scrollBar.MaxValue - _scrollBar.MinValue);
        var page = Math.Max(0d, _scrollBar.Page);
        var upperBound = GetScrollUpperBound();
        if (totalRange <= 0d || upperBound <= _scrollBar.MinValue)
        {
            return new Rect2(Vector2.Zero, new Vector2(Size.X, trackLength));
        }

        var visibleRatio = page <= 0d
            ? 0d
            : Math.Clamp(page / totalRange, 0d, 1d);
        var thumbLength = Mathf.Clamp((float)(trackLength * visibleRatio), MinimumThumbLength, trackLength);
        var travel = Math.Max(0f, trackLength - thumbLength);
        if (travel <= 0.001f)
        {
            return new Rect2(Vector2.Zero, new Vector2(Size.X, thumbLength));
        }

        var progress = (_scrollBar.Value - _scrollBar.MinValue) / (upperBound - _scrollBar.MinValue);
        var y = Mathf.Clamp((float)progress, 0f, 1f) * travel;
        return new Rect2(new Vector2(0f, y), new Vector2(Size.X, thumbLength));
    }

    private void SetScrollBarValueFromPointer(float pointerY, bool centerThumbOnPointer)
    {
        if (_scrollBar is null)
        {
            return;
        }

        var thumbRect = GetThumbRect();
        var thumbLength = Math.Max(thumbRect.Size.Y, 0f);
        var trackLength = Size.Y;
        var travel = Math.Max(0f, trackLength - thumbLength);
        if (travel <= 0.001f)
        {
            SetScrollBarValue(_scrollBar.MinValue);
            return;
        }

        var top = centerThumbOnPointer
            ? pointerY - (thumbLength * 0.5f)
            : pointerY - _thumbDragPointerOffsetY;
        var clampedTop = Mathf.Clamp(top, 0f, travel);
        var progress = clampedTop / travel;
        var upperBound = GetScrollUpperBound();
        var value = _scrollBar.MinValue + ((upperBound - _scrollBar.MinValue) * progress);
        SetScrollBarValue(value);
    }

    private void AnimateScrollBarToPointer(float pointerY, bool centerThumbOnPointer, bool snapToStep)
    {
        if (_scrollBar is null)
        {
            return;
        }

        var thumbRect = GetThumbRect();
        var thumbLength = Math.Max(thumbRect.Size.Y, 0f);
        var trackLength = Size.Y;
        var travel = Math.Max(0f, trackLength - thumbLength);
        if (travel <= 0.001f)
        {
            AnimateScrollBarToValue(_scrollBar.MinValue, snapToStep);
            return;
        }

        var top = centerThumbOnPointer
            ? pointerY - (thumbLength * 0.5f)
            : pointerY - _thumbDragPointerOffsetY;
        var clampedTop = Mathf.Clamp(top, 0f, travel);
        var progress = clampedTop / travel;
        var upperBound = GetScrollUpperBound();
        var value = _scrollBar.MinValue + ((upperBound - _scrollBar.MinValue) * progress);
        AnimateScrollBarToValue(value, snapToStep);
    }

    private void SetScrollBarValue(double value)
    {
        if (_scrollBar is null)
        {
            return;
        }

        StopScrollTween();
        _scrollBar.Value = Math.Clamp(value, _scrollBar.MinValue, GetScrollUpperBound());
    }

    private void SnapScrollBarToStep()
    {
        if (_scrollBar is null)
        {
            return;
        }

        var step = Math.Max(1d, _scrollBar.Step);
        var snapped = _scrollBar.MinValue + (Math.Round((_scrollBar.Value - _scrollBar.MinValue) / step) * step);
        AnimateScrollBarToValue(snapped, snapToStep: false);
    }

    private void EndThumbDrag()
    {
        if (!_isDraggingThumb)
        {
            return;
        }

        _isDraggingThumb = false;
        SnapScrollBarToStep();
    }

    private void AnimateScrollBarToValue(double value, bool snapToStep)
    {
        if (_scrollBar is null)
        {
            return;
        }

        var targetValue = Math.Clamp(value, _scrollBar.MinValue, GetScrollUpperBound());
        if (snapToStep)
        {
            var step = Math.Max(1d, _scrollBar.Step);
            targetValue = _scrollBar.MinValue + (Math.Round((targetValue - _scrollBar.MinValue) / step) * step);
            targetValue = Math.Clamp(targetValue, _scrollBar.MinValue, GetScrollUpperBound());
        }

        StopScrollTween();
        if (Math.Abs(targetValue - _scrollBar.Value) < 0.01d)
        {
            _scrollBar.Value = targetValue;
            return;
        }

        _scrollTween = CreateTween();
        _scrollTween.SetEase(Tween.EaseType.Out);
        _scrollTween.SetTrans(Tween.TransitionType.Cubic);
        _scrollTween.TweenProperty(_scrollBar, "value", targetValue, ScrollAnimationDurationSeconds);
        _scrollTween.Finished += OnScrollTweenFinished;
    }

    private void StopScrollTween()
    {
        if (_scrollTween is null)
        {
            return;
        }

        if (GodotObject.IsInstanceValid(_scrollTween))
        {
            _scrollTween.Finished -= OnScrollTweenFinished;
            _scrollTween.Kill();
        }

        _scrollTween = null;
    }

    private void OnScrollTweenFinished()
    {
        if (_scrollTween is null)
        {
            return;
        }

        if (GodotObject.IsInstanceValid(_scrollTween))
        {
            _scrollTween.Finished -= OnScrollTweenFinished;
        }

        _scrollTween = null;
    }

    private double GetScrollUpperBound()
    {
        if (_scrollBar is null)
        {
            return 0d;
        }

        return Math.Max(_scrollBar.MinValue, _scrollBar.MaxValue - _scrollBar.Page);
    }
}
