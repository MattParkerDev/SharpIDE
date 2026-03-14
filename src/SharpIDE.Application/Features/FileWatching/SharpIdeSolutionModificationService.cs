using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

/// Does not do any file system operations, only modifies the in-memory solution model
public class SharpIdeSolutionModificationService(FileChangedService fileChangedService, ILogger<SharpIdeSolutionModificationService> logger)
{
	private readonly FileChangedService _fileChangedService = fileChangedService;
	private readonly ILogger<SharpIdeSolutionModificationService> _logger = logger;

	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	/// The directory must already exist on disk
	public async Task<SharpIdeFolder> AddDirectory(IFolderOrProject parentNode, string directoryName)
	{
		var addedDirectoryPath = Path.Combine(parentNode.ChildNodeBasePath, directoryName);
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		var sharpIdeFolder = new SharpIdeFolder(new DirectoryInfo(addedDirectoryPath), parentNode, allFiles, allFolders);

		var correctInsertionPosition = GetInsertionPosition(parentNode, sharpIdeFolder);

		parentNode.Folders.Insert(correctInsertionPosition, sharpIdeFolder);
		SolutionModel.AllFolders.AddRange((IEnumerable<SharpIdeFolder>)[sharpIdeFolder, ..allFolders]);
		foreach (var sharpIdeFile in allFiles)
		{
			var success = SolutionModel.AllFiles.TryAdd(sharpIdeFile.Path, sharpIdeFile);
			if (success is false) _logger.LogWarning("File {filePath} already exists in SolutionModel.AllFiles when adding directory {directoryPath}", sharpIdeFile.Path, addedDirectoryPath);
		}
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

		foreach (var sharpIdeFile in filesToRemove)
		{
			var success = SolutionModel.AllFiles.TryRemove(sharpIdeFile.Path, out _);
			if (success is false) _logger.LogWarning("File {filePath} not found in SolutionModel.AllFiles when removing directory {directoryPath}", sharpIdeFile.Path, folder.Path);
		}
		SolutionModel.AllFolders.RemoveRange(foldersToRemove);
		foreach (var file in filesToRemove)
		{
			await _fileChangedService.SharpIdeFileRemoved(file);
		}
	}

	public async Task MoveDirectory(IFolderOrProject destinationParentNode, SharpIdeFolder folderToMove)
	{
		var oldFolderPath = folderToMove.Path;
		var newFolderPath = Path.Combine(destinationParentNode.ChildNodeBasePath, folderToMove.Name.Value);

		var parentFolderOrProject = (IFolderOrProject)folderToMove.Parent;
		parentFolderOrProject.Folders.Remove(folderToMove);
		var insertionIndex = GetInsertionPosition(destinationParentNode, folderToMove);
		destinationParentNode.Folders.Insert(insertionIndex, folderToMove);
		folderToMove.Parent = destinationParentNode;
		folderToMove.Path = newFolderPath;

		var stack = new Stack<SharpIdeFolder>();
		stack.Push(folderToMove);

		while (stack.Count > 0)
		{
			var current = stack.Pop();

			foreach (var subfolder in current.Folders)
			{
				subfolder.Path = Path.Combine(current.Path, subfolder.Name.Value);
				stack.Push(subfolder);
			}

			foreach (var file in current.Files)
			{
				var oldPath = file.Path;
				file.Path = Path.Combine(current.Path, file.Name.Value);
				await _fileChangedService.SharpIdeFileMoved(file, oldPath);
			}
		}
	}

	public async Task RenameDirectory(SharpIdeFolder folder, string renamedFolderName)
	{
		var oldFolderPath = folder.Path;

		folder.Name.Value = renamedFolderName;
		folder.Path = Path.Combine(Path.GetDirectoryName(oldFolderPath)!, renamedFolderName);

		var parentFolderOrProject = (IFolderOrProject)folder.Parent;
		var currentPosition = parentFolderOrProject.Folders.IndexOf(folder);
		var insertionPosition = GetMovePosition(parentFolderOrProject, folder);
		if (currentPosition != insertionPosition) parentFolderOrProject.Folders.Move(currentPosition, insertionPosition);

		var stack = new Stack<SharpIdeFolder>();
		stack.Push(folder);

		while (stack.Count > 0)
		{
			var current = stack.Pop();

			foreach (var subfolder in current.Folders)
			{
				subfolder.Path = Path.Combine(current.Path, subfolder.Name.Value);
				stack.Push(subfolder);
			}

			foreach (var file in current.Files)
			{
				var oldPath = file.Path;
				file.Path = Path.Combine(current.Path, file.Name.Value);
				await _fileChangedService.SharpIdeFileMoved(file, oldPath);
			}
		}
	}

	public async Task<SharpIdeFile> CreateFile(IFolderOrProject parentNode, string newFilePath, string fileName, string contents)
	{
		var sharpIdeFile = new SharpIdeFile(newFilePath, fileName, Path.GetExtension(newFilePath), parentNode, []);

		var correctInsertionPosition = GetInsertionPosition(parentNode, sharpIdeFile);

		parentNode.Files.Insert(correctInsertionPosition, sharpIdeFile);
		var success = SolutionModel.AllFiles.TryAdd(sharpIdeFile.Path, sharpIdeFile);
		if (success is false) _logger.LogWarning("File {filePath} already exists in SolutionModel.AllFiles when creating file", sharpIdeFile.Path);
		await _fileChangedService.SharpIdeFileAdded(sharpIdeFile, contents);
		return sharpIdeFile;
	}

	private static int GetInsertionPosition(IFolderOrProject parentNode, IFileOrFolder fileOrFolder)
	{
		var correctInsertionPosition = fileOrFolder switch
		{
			SharpIdeFile f => parentNode.Files.list.BinarySearch(f, SharpIdeFileComparer.Instance),
			SharpIdeFolder d => parentNode.Folders.list.BinarySearch(d, SharpIdeFolderComparer.Instance),
			_ => throw new InvalidOperationException("Unknown file or folder type")
		};
		if (correctInsertionPosition < 0)
		{
			correctInsertionPosition = ~correctInsertionPosition;
		}
		else
		{
			throw new InvalidOperationException("File already exists in the containing folder or project");
		}

		return correctInsertionPosition;
	}

	private static int GetMovePosition(IFolderOrProject parentNode, IFileOrFolder fileOrFolder)
	{
		var correctInsertionPosition = fileOrFolder switch
		{
			SharpIdeFile f => parentNode.Files.list
				.FindAll(x => x != f) // TODO: Investigate allocations
				.BinarySearch(f, SharpIdeFileComparer.Instance),
			SharpIdeFolder d => parentNode.Folders.list
				.FindAll(x => x != d) // TODO: Investigate allocations
				.BinarySearch(d, SharpIdeFolderComparer.Instance),
			_ => throw new InvalidOperationException("Unknown file or folder type")
		};

		if (correctInsertionPosition < 0)
		{
			correctInsertionPosition = ~correctInsertionPosition;
		}
		else
		{
			throw new InvalidOperationException("File already exists in the containing folder or project");
		}

		return correctInsertionPosition;
	}

	public async Task RemoveFile(SharpIdeFile file)
	{
		var parentFolderOrProject = (IFolderOrProject)file.Parent;
		parentFolderOrProject.Files.Remove(file);
		var success = SolutionModel.AllFiles.TryRemove(file.Path, out _);
		if (success is false) _logger.LogWarning("File {filePath} not found in SolutionModel.AllFiles when removing file", file.Path);
		await _fileChangedService.SharpIdeFileRemoved(file);
	}

	public async Task<SharpIdeFile> MoveFile(IFolderOrProject destinationParentNode, SharpIdeFile fileToMove)
	{
		var oldPath = fileToMove.Path;
		var newFilePath = Path.Combine(destinationParentNode.ChildNodeBasePath, fileToMove.Name.Value);
		var parentFolderOrProject = (IFolderOrProject)fileToMove.Parent;
		parentFolderOrProject.Files.Remove(fileToMove);
		var insertionIndex = GetInsertionPosition(destinationParentNode, fileToMove);
		destinationParentNode.Files.Insert(insertionIndex, fileToMove);
		fileToMove.Parent = destinationParentNode;
		fileToMove.Path = newFilePath;
		await _fileChangedService.SharpIdeFileMoved(fileToMove, oldPath);
		return fileToMove;
	}

	public async Task<SharpIdeFile> RenameFile(SharpIdeFile fileToRename, string renamedFileName)
	{
		var oldPath = fileToRename.Path;
		var newFilePath = Path.Combine(Path.GetDirectoryName(oldPath)!, renamedFileName);
		fileToRename.Name.Value = renamedFileName;
		fileToRename.Path = newFilePath;
		var parentFolderOrProject = (IFolderOrProject)fileToRename.Parent;
		var currentPosition = parentFolderOrProject.Files.IndexOf(fileToRename);
		var insertionPosition = GetMovePosition(parentFolderOrProject, fileToRename);
		if (currentPosition != insertionPosition) parentFolderOrProject.Files.Move(currentPosition, insertionPosition);
		await _fileChangedService.SharpIdeFileRenamed(fileToRename, oldPath);
		return fileToRename;
	}
}
