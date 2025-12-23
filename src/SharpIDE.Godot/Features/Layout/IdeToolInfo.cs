using Godot;

namespace SharpIDE.Godot.Features.Layout;

public enum ToolAnchor
{
    None = 0,
    LeftTop = 1,
    RightTop = 2,
    BottomLeft = 3,
    BottomRight = 4
}

public record IdeToolInfo(IdeTool Tool, ToolAnchor Anchor, bool IsPinned, bool IsVisible, Control Control, Texture2D Icon)
{
    public bool IsPinned { get; set; } = IsPinned;
    public bool IsVisible { get; set; } = IsVisible;
}