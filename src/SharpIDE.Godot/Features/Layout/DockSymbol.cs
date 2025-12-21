using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class DockSymbol : Control
{
	[Export]
	public DockScope DockScope { get; set; }
	
	[Export]
	public DockPosition DockPosition { get; set; }
}
