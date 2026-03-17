using Godot;

namespace SharpIDE.Godot.Features.CustomControls;

[GlobalClass, Tool]
public partial class InvertedVSplitContainer : VSplitContainer
{
    [Export]
    private int _invertedOffset = 200;
    private int _minimumBottomHeight = 30;
    private bool _invertedCollapsed = false;

    private int _separationWhenUncollapsed;

    public void InvertedSetCollapsed(bool collapsed)
    {
        if (_invertedCollapsed == collapsed) return;
        _invertedCollapsed = collapsed;
        if (collapsed)
        {
            ApplySplitOffset((int)Size.Y + 100);
            DraggingEnabled = false;
            AddThemeConstantOverride(ThemeStringNames.Separation, 0);
        }
        else
        {
            ApplySplitOffset(ClampExpandedSplitOffset((int)Size.Y - _invertedOffset));
            DraggingEnabled = true;
            AddThemeConstantOverride(ThemeStringNames.Separation, _separationWhenUncollapsed);
        }
    }

    public void RefreshBottomHeightConstraint()
    {
        if (_invertedCollapsed) return;

        var splitOffset = ClampExpandedSplitOffset(GetCurrentSplitOffset());
        ApplySplitOffset(splitOffset);
        _invertedOffset = (int)Size.Y - splitOffset;
    }

    public override void _Ready()
    {
        _separationWhenUncollapsed = GetThemeConstant(ThemeStringNames.Separation);
        Dragged += OnDragged;
    }

    private void OnDragged(long offset)
    {
        var clampedOffset = ClampExpandedSplitOffset((int)offset);
        if (clampedOffset != offset)
        {
            ApplySplitOffset(clampedOffset);
        }

        _invertedOffset = (int)Size.Y - clampedOffset;
    }
    
    public override void _Notification(int what)
    {
        if (what == NotificationResized && _invertedCollapsed is false)
        {
            ApplySplitOffset(ClampExpandedSplitOffset((int)Size.Y - _invertedOffset));
        }
    }

    private int ClampExpandedSplitOffset(int requestedSplitOffset)
    {
        var maxSplitOffset = Math.Max(0, (int)Size.Y - GetMinimumExpandedBottomHeight());
        return Math.Clamp(requestedSplitOffset, 0, maxSplitOffset);
    }

    private int GetMinimumExpandedBottomHeight()
    {
        var bottomChild = GetChildCount() > 1 ? GetChildOrNull<Control>(1) : null;
        if (bottomChild is null)
        {
            return Math.Max(0, _minimumBottomHeight);
        }

        return Math.Max(_minimumBottomHeight, (int)Math.Ceiling(bottomChild.CustomMinimumSize.Y));
    }
    private void ApplySplitOffset(int splitOffset)
    {
        var offsets = SplitOffsets;
        if (offsets.Length == 0)
        {
            offsets = [splitOffset];
        }
        else
        {
            offsets[0] = splitOffset;
        }

        SplitOffsets = offsets;
    }

    private int GetCurrentSplitOffset()
    {
        var offsets = SplitOffsets;
        return offsets.Length > 0 ? offsets[0] : 0;
    }
}
