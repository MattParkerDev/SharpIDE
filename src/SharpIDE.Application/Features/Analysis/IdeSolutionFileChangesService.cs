using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Analysis;

/// Applies the file changes computed by Roslyn (code actions, renames) - to open editors, disk, the file tree and the workspace
public class IdeSolutionFileChangesService(FileChangedService fileChangedService, IdeFileOperationsService fileOperationsService, SharpIdeRootFolderModificationService rootFolderModificationService, ILogger<IdeSolutionFileChangesService> logger)
{
	private readonly FileChangedService _fileChangedService = fileChangedService;
	private readonly IdeFileOperationsService _fileOperationsService = fileOperationsService;
	private readonly SharpIdeRootFolderModificationService _rootFolderModificationService = rootFolderModificationService;
	private readonly ILogger<IdeSolutionFileChangesService> _logger = logger;

	public async Task ApplyAsync(SolutionFileChanges changes)
	{
		if (changes.IsEmpty) return;
		var allFiles = _rootFolderModificationService.FileTree.AllFiles;

		// Text changes first, to files at their old paths. Files outside the file tree (e.g. an .editorconfig above the solution folder) can't be open, so are written to disk.
		var textEdits = new List<SharpIdeFileTextEdit>();
		var textEditsOutsideFileTree = new List<(string FilePath, SourceText NewText)>();
		foreach (var change in changes.ChangedFiles)
		{
			if (allFiles.TryGetValue(change.FilePath, out var file)) textEdits.Add(new SharpIdeFileTextEdit(file, change.OldText, change.Changes));
			else textEditsOutsideFileTree.Add((change.FilePath, change.OldText.WithChanges(change.Changes)));
		}
		await _fileChangedService.ApplyTextEdits(textEdits);
		await _fileChangedService.ApplyTextEditsOutsideFileTree(textEditsOutsideFileTree);

		// Then renames (e.g. "Rename file to match type") - once the edits to open files have reached Roslyn, so they apply at the path they were made at
		if (changes.RenamedFiles.Count > 0) await _fileChangedService.WaitForOpenFileTextChangesAsync();
		foreach (var rename in changes.RenamedFiles)
		{
			if (allFiles.TryGetValue(rename.OldFilePath, out var file) && await _fileOperationsService.MoveFileToPath(file, rename.NewFilePath)) continue;
			_logger.LogWarning("Not renaming '{OldFilePath}' to '{NewFilePath}' - only files in the file tree can be renamed", rename.OldFilePath, rename.NewFilePath);
		}

		// Then removals and additions (e.g. "Move type to file", or a new .editorconfig) - in one workspace change
		await using var _ = _fileChangedService.BeginBatch();
		foreach (var filePath in changes.RemovedFilePaths)
		{
			if (allFiles.TryGetValue(filePath, out var file))
			{
				await _fileOperationsService.DeleteFile(file);
			}
			else
			{
				File.Delete(filePath);
				await _fileChangedService.FileOutsideFileTreeRemoved(filePath);
			}
		}
		foreach (var addition in changes.AddedFiles)
		{
			var contents = addition.Text.ToString();
			if (await _fileOperationsService.CreateFileAtPath(addition.FilePath, contents) is not null) continue;
			// Outside the file tree, so not watched - written to disk, and picked up by the project system on its next design-time build
			Directory.CreateDirectory(Path.GetDirectoryName(addition.FilePath)!);
			await File.WriteAllTextAsync(addition.FilePath, contents);
			_logger.LogInformation("Created '{FilePath}', which is outside the file tree", addition.FilePath);
		}
	}

	/// The file in the file tree at this path, e.g. to navigate to once a code action is applied
	public SharpIdeFile? GetFile(string filePath) => _rootFolderModificationService.FileTree.AllFiles.GetValueOrDefault(filePath);
}
