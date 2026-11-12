using Microsoft.CodeAnalysis;
using SharpIDE.Application.Features.FileWatching;

namespace SharpIDE.Application.Features.Analysis;

public class IdeRenameService(RoslynAnalysis roslynAnalysis, FileChangedService fileChangedService)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly FileChangedService _fileChangedService = fileChangedService;

	public async Task ApplyRename(ISymbol symbol, string newName)
	{
		var edits = await _roslynAnalysis.GetRenameApplyChanges(symbol, newName);
		await _fileChangedService.ApplyTextEdits(edits);
	}
}
