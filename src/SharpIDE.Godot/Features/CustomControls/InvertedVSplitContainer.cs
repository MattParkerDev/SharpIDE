using Godot;

namespace SharpIDE.Godot.Features.CustomControls;

[GlobalClass, Tool]
public partial class InvertedVSplitContainer : VSplitContainer
{
	[Export]
	private int _invertedOffset = 200;
	private bool _invertedCollapsed = false;

	public void InvertedSetCollapsed(bool collapsed)
	{
		if (_invertedCollapsed == collapsed) return;
		_invertedCollapsed = collapsed;
		if (collapsed)
		{
			SplitOffset = (int)Size.Y + 100;
			DraggingEnabled = false;
		}
		else
		{
			SplitOffset = (int)Size.Y - _invertedOffset;
			DraggingEnabled = true;
		}
	}

	public override void _Ready()
	{
		Dragged += OnDragged;
	}

	private void OnDragged(long offset)
	{
		_invertedOffset = (int)Size.Y - SplitOffset;
	}
	
	public override void _Notification(int what)
	{
		if (what == NotificationResized)
		{
			SplitOffset = (int)Size.Y - _invertedOffset;
		}
	}
}
