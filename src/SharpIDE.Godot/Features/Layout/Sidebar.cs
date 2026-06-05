using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class Sidebar : Panel
{
    public Control ToolPreview = null!;

    [Export]
    public Vector2 ToolMinimumSize { get; set; } = Vector2.Zero;

    public Container TopTools { get; private set; } = null!;

    public Container BottomTools { get; private set; } = null!;

    public override void _Ready()
    {
        TopTools = GetNode<Container>("%TopTools");
        BottomTools = GetNode<Container>("%BottomTools");

        ToolPreview = CreateToolPreview();
    }

    private ColorRect CreateToolPreview()
    {
        var rect = new ColorRect();
        rect.Color = GetThemeColor(ThemeStringNames.DropHighlightColor);
        rect.CustomMinimumSize = ToolMinimumSize;
        rect.SizeFlagsHorizontal = SizeFlags.Fill;
        rect.SizeFlagsVertical = SizeFlags.Fill;
        return rect;
    }
}