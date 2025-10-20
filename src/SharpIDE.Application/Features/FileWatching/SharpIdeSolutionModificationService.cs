using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

/// Does not do any file system operations, only modifies the in-memory solution model
public class SharpIdeSolutionModificationService(FileChangedService fileChangedService)
{
	private readonly FileChangedService _fileChangedService = fileChangedService;

	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	/// The directory must already exist on disk
	public async Task<SharpIdeFolder> AddDirectory(SharpIdeFolder parentFolder, string directoryName)
	{
		var addedDirectoryPath = Path.Combine(parentFolder.Path, directoryName);
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		var sharpIdeFolder = new SharpIdeFolder(new DirectoryInfo(addedDirectoryPath), parentFolder, allFiles, allFolders);
		parentFolder.Folders.Add(sharpIdeFolder);
		SolutionModel.AllFolders.AddRange((IEnumerable<SharpIdeFolder>)[sharpIdeFolder, ..allFolders]);
		SolutionModel.AllFiles.AddRange(allFiles);
		foreach (var file in allFiles)
		{
			await _fileChangedService.SharpIdeFileAdded(file, await File.ReadAllTextAsync(file.Path));
		}
		return sharpIdeFolder;
	}

	public async Task RemoveDirectory(SharpIdeFolder folder)
	{
		var parentFolderOrProject = (IFolderOrProject)folder.Parent;
		parentFolderOrProject.Folders.Remove(folder);

		// Also remove all child files and folders from SolutionModel.AllFiles and AllFolders
		var foldersToRemove = new List<SharpIdeFolder>();

		var stack = new Stack<SharpIdeFolder>();
		stack.Push(folder);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			foldersToRemove.Add(current);

			foreach (var subfolder in current.Folders)
			{
				stack.Push(subfolder);
			}
		}

		var filesToRemove = foldersToRemove.SelectMany(f => f.Files).ToList();

		SolutionModel.AllFiles.RemoveRange(filesToRemove);
		SolutionModel.AllFolders.RemoveRange(foldersToRemove);
		foreach (var file in filesToRemove)
		{
			await _fileChangedService.SharpIdeFileRemoved(file);
		}
	}

	public async Task CreateFile(SharpIdeFolder parentFolder, string fileName, string contents)
	{
		var newFilePath = Path.Combine(parentFolder.Path, fileName);
		var sharpIdeFile = new SharpIdeFile(newFilePath, fileName, parentFolder, []);
		parentFolder.Files.Add(sharpIdeFile);
		SolutionModel.AllFiles.Add(sharpIdeFile);
		await _fileChangedService.SharpIdeFileAdded(sharpIdeFile, contents);
	}
}
