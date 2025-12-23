using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class Sidebar : Panel
{
	public Control TopTools { get; private set; } = null!;
	public Control BottomTools { get; private set; } = null!;

	public override void _Ready()
	{
		TopTools = GetNode<Control>("%TopTools");
		BottomTools = GetNode<Control>("%BottomTools");
	}
}
