namespace SharpIDE.Godot.Features.ToolPanes;

public enum ToolPaneLocation
{
	Unknown, // Unfortunately, godot doesn't support nullable value types for interop
	TopLeft,
	TopRight,
	BottomLeft,
	BottomRight,
}
