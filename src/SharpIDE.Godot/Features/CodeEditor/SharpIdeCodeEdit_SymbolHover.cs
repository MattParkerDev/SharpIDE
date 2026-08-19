using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private SymbolHoverPopup _symbolHoverPopup = null!;

	private void CloseSymbolHoverWindow() => _symbolHoverPopup.Close();

	private async void OnSymbolHovered(string symbol, long line, long column)
	{
		if (Visible is false)
		{
			return;
		}

		var linePosition = new LinePosition((int)line, (int)column);
		var (roslynSymbol, symbolSpan) = await _roslynAnalysis.LookupSymbol(_currentFile, linePosition);
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
	}
}
