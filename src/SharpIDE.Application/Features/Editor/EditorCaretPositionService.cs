namespace SharpIDE.Application.Features.Editor;

/// <summary>
/// Used by code editor windows to report the current caret position for display elsewhere in the UI
/// </summary>
public class EditorCaretPositionService
{
	public (int, int) CaretPosition { get; set; } = (1, 1);
	public (int characters, int lineBreaks)? SelectionInfo { get; set; }
}
