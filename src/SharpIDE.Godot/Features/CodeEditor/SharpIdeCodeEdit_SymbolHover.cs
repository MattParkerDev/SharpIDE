using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private SymbolHoverPopup _symbolHoverPopup = null!;

	private void CloseSymbolHoverWindow() => _symbolHoverPopup.Close();

	[RequiresGodotUiThread]
	private void OnSymbolHovered(string symbol, long line, long column)
	{
		if (Visible is false)
		{
			return;
		}

		var file = _currentFile;
		var linePosition = new LinePosition((int)line, (int)column);
		// The lookup binds the document's semantic model, which runs synchronously once Roslyn has it cached - so it's kept off the UI thread
		_ = Task.GodotRun(async () =>
		{
			var (roslynSymbol, symbolSpan) = await _roslynAnalysis.LookupSymbol(file, linePosition);
			await this.InvokeAsync(() =>
			{
				// The tab may have been closed, or switched to another file, while the symbol was looked up
				if (IsInstanceValid(this) is false || IsVisibleInTree() is false || _currentFile != file) return;
				var diagnostic = _fileDiagnostics
					.AsValueEnumerable()
					.Concat(_fileAnalyzerDiagnostics)
					.Concat(_projectDiagnosticsForFile)
					.FirstOrDefault(candidate => linePosition >= candidate.Span.Start && linePosition <= candidate.Span.End);
				if (roslynSymbol is null && diagnostic is null)
				{
					return;
				}

				_symbolHoverPopup.Show(roslynSymbol, symbolSpan, diagnostic, linePosition);
			});
		});
	}
}
