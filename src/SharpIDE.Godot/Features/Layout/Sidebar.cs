using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class Sidebar : Panel
{
	[Export]
	public Vector2 ToolMinimumSize { get; set; } = Vector2.Zero;
	
	public Container TopTools { get; private set; } = null!;
	public Container BottomTools { get; private set; } = null!;

	[Export]
	public Color PreviewColor { get; set; }

	public Control ToolPreview = null!;

	public override void _Ready()
	{
		TopTools = GetNode<Container>("%TopTools");
		BottomTools = GetNode<Container>("%BottomTools");

		ToolPreview = CreateToolPreview();
	}

	private ColorRect CreateToolPreview()
	{
		var rect = new ColorRect();
		rect.Color = PreviewColor;
		rect.CustomMinimumSize = ToolMinimumSize;
		rect.SizeFlagsHorizontal = SizeFlags.Fill;
		rect.SizeFlagsVertical = SizeFlags.Fill;
		return rect;
	}
}
