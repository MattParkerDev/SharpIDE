using Godot;

namespace SharpIDE.Godot.Features.CustomControls;

[GlobalClass, Tool]
public partial class InvertedVSplitContainer : VSplitContainer
{
	[Export]
	private int _invertedOffset = 200;
	private bool _invertedCollapsed = false;

	private int _separationWhenUncollapsed;

	public void InvertedSetCollapsed(bool collapsed)
	{
		if (_invertedCollapsed == collapsed) return;
		_invertedCollapsed = collapsed;
		if (collapsed)
		{
			SplitOffset = (int)Size.Y + 100;
			DraggingEnabled = false;
			AddThemeConstantOverride(ThemeStringNames.Separation, 0);
		}
		else
		{
			SplitOffset = (int)Size.Y - _invertedOffset;
			DraggingEnabled = true;
			AddThemeConstantOverride(ThemeStringNames.Separation, _separationWhenUncollapsed);
		}
	}

	public override void _Ready()
	{
		_separationWhenUncollapsed = GetThemeConstant(ThemeStringNames.Separation);
		Dragged += OnDragged;
	}

	private void OnDragged(long offset)
	{
		_invertedOffset = (int)Size.Y - SplitOffset;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized && _invertedCollapsed is false)
		{
			SplitOffset = (int)Size.Y - _invertedOffset;
		}
	}
}
