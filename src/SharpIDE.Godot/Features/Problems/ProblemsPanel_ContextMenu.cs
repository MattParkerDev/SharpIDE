using Godot;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.Problems;

file enum DiagnosticContextMenuOptions
{
	Copy = 0
}

public partial class ProblemsPanel
{
	private void TreeOnItemMouseSelected(Vector2 mousePosition, long mouseButtonIndex)
	{
		if ((MouseButton)mouseButtonIndex is not MouseButton.Right) return;
		var diagnostic = _tree.GetSelected()?.SharpIdeDiagnostic;
		if (diagnostic is null) return;

		var globalMousePosition = _tree.GlobalPosition + mousePosition;
		OpenContextMenuDiagnostic(diagnostic, globalMousePosition);
	}

	private void OpenContextMenuDiagnostic(SharpIdeDiagnostic diagnostic, Vector2 globalMousePosition)
	{
		var menu = new PopupMenu();
		AddChild(menu);
		menu.AddItem("Copy", (int)DiagnosticContextMenuOptions.Copy);
		menu.PopupHide += menu.QueueFree;
		menu.IdPressed += id =>
		{
			var actionId = (DiagnosticContextMenuOptions)id;
			if (actionId is DiagnosticContextMenuOptions.Copy)
			{
				DisplayServer.ClipboardSet(diagnostic.Message);
			}
		};

		menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
		menu.Popup();
	}
}
