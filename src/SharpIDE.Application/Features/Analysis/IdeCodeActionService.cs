using Microsoft.CodeAnalysis.CodeActions;
using SharpIDE.Application.Features.FileWatching;

namespace SharpIDE.Application.Features.Analysis;

public class IdeCodeActionService(RoslynAnalysis roslynAnalysis, FileChangedService fileChangedService)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly FileChangedService _fileChangedService = fileChangedService;

	public async Task ApplyCodeAction(CodeAction codeAction)
	{
		var affectedFiles = await _roslynAnalysis.GetCodeActionApplyChanges(codeAction);
		foreach (var (affectedFile, updatedText) in affectedFiles)
		{
			await _fileChangedService.SharpIdeFileChanged(affectedFile, updatedText, FileChangeType.CodeActionChange);
		}
	}
}
