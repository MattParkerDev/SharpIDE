using Microsoft.CodeAnalysis.CodeActions;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Analysis;

public class IdeCodeActionService(RoslynAnalysis roslynAnalysis, IdeSolutionFileChangesService solutionFileChangesService)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly IdeSolutionFileChangesService _solutionFileChangesService = solutionFileChangesService;

	/// Applies the code action's file changes (edits, and added, removed and renamed files - including .editorconfig).
	/// Returns where to navigate afterwards, if the code action asked for it (e.g. a generated member, or a new file).
	public async Task<(SharpIdeFile File, SharpIdeFileLinePosition Position)?> ApplyCodeAction(CodeAction codeAction)
	{
		var (changes, navigation) = await _roslynAnalysis.GetCodeActionApplyChanges(codeAction);
		await _solutionFileChangesService.ApplyAsync(changes);
		if (navigation is null || _solutionFileChangesService.GetFile(navigation.FilePath) is not { } file) return null;
		return (file, new SharpIdeFileLinePosition(navigation.Position.Line, navigation.Position.Character));
	}
}
