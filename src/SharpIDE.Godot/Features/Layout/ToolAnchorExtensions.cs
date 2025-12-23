namespace SharpIDE.Godot.Features.Layout;

public static class ToolAnchorExtensions
{
    extension(ToolAnchor anchor)
    {
        public bool IsLeft()
        {
            return anchor is ToolAnchor.LeftTop or ToolAnchor.BottomLeft;
        }

        public bool IsRight()
        {
            return anchor is ToolAnchor.RightTop or ToolAnchor.BottomRight;
        }

        public bool IsTop()
        {
            return anchor is ToolAnchor.LeftTop or ToolAnchor.RightTop;
        }

        public bool IsBottom()
        {
            return anchor is ToolAnchor.BottomLeft or ToolAnchor.BottomRight;
        }
    }
}