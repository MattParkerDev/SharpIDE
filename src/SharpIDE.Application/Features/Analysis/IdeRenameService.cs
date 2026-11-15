using Microsoft.CodeAnalysis;

namespace SharpIDE.Application.Features.Analysis;

public class IdeRenameService(RoslynAnalysis roslynAnalysis, IdeSolutionFileChangesService solutionFileChangesService)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly IdeSolutionFileChangesService _solutionFileChangesService = solutionFileChangesService;

	public async Task ApplyRename(ISymbol symbol, string newName)
	{
		var changes = await _roslynAnalysis.GetRenameApplyChanges(symbol, newName);
		await _solutionFileChangesService.ApplyAsync(changes);
	}
}
