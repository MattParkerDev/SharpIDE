using Microsoft.CodeAnalysis.CodeActions;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Analysis;

public class IdeCodeActionService(RoslynAnalysis roslynAnalysis, IdeSolutionFileChangesService solutionFileChangesService)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly IdeSolutionFileChangesService _solutionFileChangesService = solutionFileChangesService;

	public static bool IsFixAll(CodeAction codeAction) => RoslynAnalysis.IsFixAllCodeAction(codeAction);

	/// Applies the code action's file changes (edits, and added, removed and renamed files - including .editorconfig).
	/// Returns where to navigate afterwards, if the code action asked for it (e.g. a generated member, or a new file).
	/// Cancellation only applies while the changes are computed - once they start being applied, they are applied in full.
	public async Task<(SharpIdeFile File, SharpIdeFileLinePosition Position)?> ApplyCodeAction(CodeAction codeAction, CodeActionProgress? progress = null, CancellationToken cancellationToken = default)
	{
		var (changes, navigation) = await _roslynAnalysis.GetCodeActionApplyChanges(codeAction, progress, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		var fileCount = changes.ChangedFiles.Count + changes.AddedFiles.Count + changes.RemovedFilePaths.Count + changes.RenamedFiles.Count;
		progress?.StartApplying($"Applying changes to {fileCount} file{(fileCount is 1 ? "" : "s")}");
		await _solutionFileChangesService.ApplyAsync(changes);
		if (navigation is null || _solutionFileChangesService.GetFile(navigation.FilePath) is not { } file) return null;
		return (file, new SharpIdeFileLinePosition(navigation.Position.Line, navigation.Position.Character));
	}
}
