using Microsoft.CodeAnalysis.Threading;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private readonly AsyncBatchingWorkQueue _selectionChangedQueue;

	private async ValueTask ProcessSelectionChanged(CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested) return;
		string? selectedText = null;
		await this.InvokeAsync(() =>
		{
			if (HasSelection() is false) return;
			selectedText = GetSelectedText();
		});
		if (string.IsNullOrWhiteSpace(selectedText)) return;
		var lineBreakCount = 0;
		var slashRsToRemove = 0;

		foreach (var c in selectedText.AsSpan())
		{
			if (c is '\n') lineBreakCount++;
			else if (c is '\r') slashRsToRemove++;
		}

		var charLength = selectedText.Length - lineBreakCount - slashRsToRemove;
		_editorCaretPositionService.SelectionInfo = (charLength, lineBreakCount);
	}
}
