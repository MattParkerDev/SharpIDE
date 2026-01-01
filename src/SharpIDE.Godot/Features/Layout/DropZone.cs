using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class DropZone : Control
{
	public ColorRect Highlight { get; set; } = null!;
	
	public override void _Ready()
	{
		Highlight = GetNode<ColorRect>("Highlight");
	}
}
