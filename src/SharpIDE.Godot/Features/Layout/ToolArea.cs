using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class ToolArea : Control
{
	public Control? CurrentTool { get; private set; }

	public override void _Ready()
	{
		Visible = false;
	}

	public void ShowTool(Control tool)
	{
		if (ReferenceEquals(CurrentTool, tool))
		{
			return;
		}

		if (CurrentTool is not null)
		{
			HideTool();
		}

		if (tool.GetParent() is ToolArea parent)
		{
			parent.HideTool();
		}

		CurrentTool = tool;

		AddChild(CurrentTool);
		Visible = true;
	}

	public void HideTool()
	{
		if (CurrentTool is not null)
		{
			RemoveChild(CurrentTool);
		}

		CurrentTool = null;
		Visible = false;
	}
}
