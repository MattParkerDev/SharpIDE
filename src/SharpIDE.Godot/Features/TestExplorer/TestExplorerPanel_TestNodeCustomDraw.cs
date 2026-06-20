using Godot;
using SharpIDE.Application.Features.Testing.Client;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestExplorerPanel
{
	private Callable? _testNodeCustomDrawCallable;
	private TextLine _testNodeTextLine = new TextLine(); // Reusing this is based on the assumption that it is called by godot in a single-threaded fashion
	private void TestNodeCustomDraw(TreeItem treeItem, Rect2 rect)
	{
		var hovered = _testNodesTree.GetItemAtPosition(_testNodesTree.GetLocalMousePosition()) == treeItem;
		var isSelected = treeItem.IsSelected(0);

		var testNode = treeItem.SharpIdeTestNode;
		if (testNode is null) return;

		var displayName = testNode.LocationMethod;
		var executionState = testNode.ExecutionState;

		const float padding = 4.0f;
		const float spacing = 6.0f;

		var currentX = rect.Position.X + padding;
		var currentY = rect.Position.Y;

		// Get font and prepare text
		var font = _testNodesTree.GetThemeFont(ThemeStringNames.Font);
		var fontSize = _testNodesTree.GetThemeFontSize(ThemeStringNames.FontSize);
		var textColor = (isSelected, hovered) switch
		{
			(true, true) => _testNodesTree.GetThemeColor(ThemeStringNames.FontHoveredSelectedColor),
			(true, false) => _testNodesTree.GetThemeColor(ThemeStringNames.FontSelectedColor),
			(false, true) => _testNodesTree.GetThemeColor(ThemeStringNames.FontHoveredColor),
			(false, false) => _testNodesTree.GetThemeColor(ThemeStringNames.FontColor)
		};
		var textYPos = currentY + (rect.Size.Y + fontSize) / 2 - 2;

		// Draw test name on the left with ellipsis truncation
		var textLine = _testNodeTextLine;
		textLine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		textLine.SetHorizontalAlignment(HorizontalAlignment.Left);
		textLine.AddString(displayName, font, fontSize);

		var statusWidth = font.GetStringSize(executionState, HorizontalAlignment.Left, -1, fontSize).X;
		var maxNameWidth = rect.Position.X + rect.Size.X - currentX - spacing - statusWidth - padding;
		float nameWidth = 0;
		if (maxNameWidth > 0)
		{
			textLine.Width = maxNameWidth;
			textLine.Draw(_testNodesTree.GetCanvasItem(), new Vector2(currentX, textYPos - textLine.GetLineAscent()), textColor);
			nameWidth = textLine.GetLineWidth();
			textLine.Clear();
		}

		// Draw execution state immediately after the test name
		var statusColor = GetTextColour(executionState);
		_testNodesTree.DrawString(font, new Vector2(currentX + nameWidth + spacing, textYPos), executionState, HorizontalAlignment.Left, -1, fontSize, statusColor);
	}

	private static Color GetTextColour(string executionState)
	{
		var colour = executionState switch
		{
			ExecutionStates.Passed => SuccessTextColour,
			ExecutionStates.InProgress => RunningTextColour,
			ExecutionStates.Discovered => PendingTextColour,
			ExecutionStates.Failed => FailedTextColour,
			ExecutionStates.Cancelled => CancelledTextColour,
			ExecutionStates.Skipped => SkippedTextColour,
			_ => Colors.White,
		};
		return colour;
	}

	private static readonly Color SuccessTextColour = new Color("499c54");
	private static readonly Color RunningTextColour = new Color("a77fd2");
	private static readonly Color PendingTextColour = new Color("2aa9e7");
	private static readonly Color FailedTextColour = new Color("c65344");
	private static readonly Color CancelledTextColour = new Color("e4a631");
	private static readonly Color SkippedTextColour = new Color("c0c0c0");
}
