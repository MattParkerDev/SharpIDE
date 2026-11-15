using Godot;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private readonly PackedScene _renameSymbolDialogScene = ResourceLoader.Load<PackedScene>("uid://cfcgmyhahblw");
	[Inject] private readonly IdeRenameService _ideRenameService = null!;
	public async Task RenameSymbol()
	{
		var cursorPosition = GetCaretPosition();
		var (roslynSymbol, linePositionSpan) = await _roslynAnalysis.LookupSymbol(_currentFile, new LinePosition(cursorPosition.line, cursorPosition.col));
		if (roslynSymbol is null || linePositionSpan is null)
		{
			GD.Print("No symbol found at cursor position for renaming.");
			return;
		}
		if (roslynSymbol.IsFromSource() is false) return;

		var renameSymbolDialog = _renameSymbolDialogScene.Instantiate<RenameSymbolDialog>();
		renameSymbolDialog.SymbolName = roslynSymbol.Name;
		await this.InvokeAsync(() =>
		{
			AddChild(renameSymbolDialog);
			renameSymbolDialog.PopupCentered();
		});
		var newName = await renameSymbolDialog.RenameTaskCompletionSource.Task;
		renameSymbolDialog.QueueFree();
		if (string.IsNullOrWhiteSpace(newName) || newName == roslynSymbol.Name)
		{
			GD.Print("Renaming cancelled or no change in name.");
			return;
		}
		await _ideRenameService.ApplyRename(roslynSymbol, newName);
	}
}
