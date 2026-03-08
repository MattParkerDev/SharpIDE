using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class DropZone : Control
{
	private ColorRect _highlight = null!;
	
	public override void _Ready()
	{
		_highlight = GetNode<ColorRect>("Highlight");
	}

	public void ShowHighlight()
	{
		_highlight.Color = GetThemeColor(ThemeStringNames.DropHighlightColor, nameof(Sidebar));
		_highlight.Show();
	}

	public void HideHighlight()
	{
		_highlight.Hide();
	}
}
