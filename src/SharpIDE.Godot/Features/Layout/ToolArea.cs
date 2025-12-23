using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class ToolArea : Control
{
	private IdeToolInfo? _activeTool;

	public override void _Ready()
	{
		Visible = false;
	}

	public void SetActiveTool(IdeToolInfo? toolInfo)
	{
		if (ReferenceEquals(_activeTool, toolInfo))
		{
			return;
		}
		
		if (_activeTool is not null)
		{
			RemoveChild(_activeTool.Control);
			_activeTool.IsVisible = false;
		}
		
		if (toolInfo is null)
		{
			Visible = false;
			_activeTool = null;
			return;
		}

		if (toolInfo.Control.GetParent() is { } parent)
		{
			parent.RemoveChild(toolInfo.Control);
		}
		
		_activeTool = toolInfo;
		
		AddChild(_activeTool.Control);
		Visible = _activeTool.IsVisible = true;
	}
}
