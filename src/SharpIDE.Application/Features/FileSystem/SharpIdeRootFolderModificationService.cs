using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileSystem;

/// Modifies the in-memory file-system tree rooted at SharpIdeRootFolder.
/// Does not perform any disk I/O - callers (IdeFileOperationsService) are responsible for that.
public class SharpIdeRootFolderModificationService(FileChangedService fileChangedService, ILogger<SharpIdeRootFolderModificationService> logger)
{
	private readonly FileChangedService _fileChangedService = fileChangedService;
	private readonly ILogger<SharpIdeRootFolderModificationService> _logger = logger;

	public SharpIdeRootFolder RootFolder { get; set; } = null!;

	/// The directory must already exist on disk before calling this.
	public async Task<SharpIdeFolder> AddDirectory(SharpIdeFolder parentFolder, string directoryName)
	{
		var addedDirectoryPath = Path.Combine(parentFolder.ChildNodeBasePath, directoryName);
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		var sharpIdeFolder = new SharpIdeFolder(new DirectoryInfo(addedDirectoryPath), parentFolder, allFiles, allFolders);

		var correctInsertionPosition = GetInsertionPosition(parentFolder, sharpIdeFolder);
		parentFolder.Folders.Insert(correctInsertionPosition, sharpIdeFolder);

		RootFolder.AllFolders.AddRange((IEnumerable<SharpIdeFolder>)[sharpIdeFolder, ..allFolders]);
		foreach (var sharpIdeFile in allFiles)
		{
			var success = RootFolder.AllFiles.TryAdd(sharpIdeFile.Path, sharpIdeFile);
			if (success is false) _logger.LogWarning("File {filePath} already exists in AllFiles when adding directory {directoryPath}", sharpIdeFile.Path, addedDirectoryPath);
		}
		foreach (var file in allFiles)
		{
			await _fileChangedService.SharpIdeFileAdded(file, await File.ReadAllTextAsync(file.Path));
		}
		return sharpIdeFolder;
	}

	public async Task RemoveDirectory(SharpIdeFolder folder)
	{
		var parentFolder = (SharpIdeFolder)folder.Parent;
		parentFolder.Folders.Remove(folder);

		var foldersToRemove = new List<SharpIdeFolder>();
		var stack = new Stack<SharpIdeFolder>();
		stack.Push(folder);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			foldersToRemove.Add(current);
			foreach (var subfolder in current.Folders)
				stack.Push(subfolder);
		}

		var filesToRemove = foldersToRemove.SelectMany(f => f.Files).ToList();

		foreach (var sharpIdeFile in filesToRemove)
		{
			var success = RootFolder.AllFiles.TryRemove(sharpIdeFile.Path, out _);
			if (success is false) _logger.LogWarning("File {filePath} not found in AllFiles when removing directory {directoryPath}", sharpIdeFile.Path, folder.Path);
		}
		foreach (var folderToRemove in foldersToRemove)
			RootFolder.AllFolders.Remove(folderToRemove);
		foreach (var file in filesToRemove)
		{
			await _fileChangedService.SharpIdeFileRemoved(file);
		}
	}

	public async Task MoveDirectory(SharpIdeFolder destinationParentFolder, SharpIdeFolder folderToMove)
	{
		var newFolderPath = Path.Combine(destinationParentFolder.ChildNodeBasePath, folderToMove.Name.Value);

		var parentFolder = (SharpIdeFolder)folderToMove.Parent;
		parentFolder.Folders.Remove(folderToMove);
		var insertionIndex = GetInsertionPosition(destinationParentFolder, folderToMove);
		destinationParentFolder.Folders.Insert(insertionIndex, folderToMove);
		folderToMove.Parent = destinationParentFolder;
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
				if (RootFolder.AllFiles.TryRemove(oldPath, out _) is false) _logger.LogWarning("File {filePath} not found in AllFiles when moving file", oldPath);
				if (RootFolder.AllFiles.TryAdd(file.Path, file) is false) _logger.LogWarning("File {filePath} already exists in AllFiles when moving file", file.Path);
				await _fileChangedService.SharpIdeFileMoved(file, oldPath);
			}
		}
	}

	public async Task RenameDirectory(SharpIdeFolder folder, string renamedFolderName)
	{
		var oldFolderPath = folder.Path;

		folder.Name.Value = renamedFolderName;
		folder.Path = Path.Combine(Path.GetDirectoryName(oldFolderPath)!, renamedFolderName);

		var parentFolder = (SharpIdeFolder)folder.Parent;
		var currentPosition = parentFolder.Folders.IndexOf(folder);
		var insertionPosition = GetMovePosition(parentFolder, folder);
		if (currentPosition != insertionPosition) parentFolder.Folders.Move(currentPosition, insertionPosition);

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
				if (RootFolder.AllFiles.TryRemove(oldPath, out _) is false) _logger.LogWarning("File {filePath} not found in AllFiles when moving file", oldPath);
				if (RootFolder.AllFiles.TryAdd(file.Path, file) is false) _logger.LogWarning("File {filePath} already exists in AllFiles when moving file", file.Path);
				await _fileChangedService.SharpIdeFileMoved(file, oldPath);
			}
		}
	}

	public async Task<SharpIdeFile> CreateFile(SharpIdeFolder parentFolder, string newFilePath, string fileName, string contents)
	{
		var sharpIdeFile = new SharpIdeFile(newFilePath, fileName, Path.GetExtension(newFilePath), parentFolder, []);

		var correctInsertionPosition = GetInsertionPosition(parentFolder, sharpIdeFile);
		parentFolder.Files.Insert(correctInsertionPosition, sharpIdeFile);

		var success = RootFolder.AllFiles.TryAdd(sharpIdeFile.Path, sharpIdeFile);
		if (success is false) _logger.LogWarning("File {filePath} already exists in AllFiles when creating file", sharpIdeFile.Path);
		await _fileChangedService.SharpIdeFileAdded(sharpIdeFile, contents);
		return sharpIdeFile;
	}

	public async Task RemoveFile(SharpIdeFile file)
	{
		var parentFolder = (SharpIdeFolder)file.Parent;
		parentFolder.Files.Remove(file);
		var success = RootFolder.AllFiles.TryRemove(file.Path, out _);
		if (success is false) _logger.LogWarning("File {filePath} not found in AllFiles when removing file", file.Path);
		await _fileChangedService.SharpIdeFileRemoved(file);
	}

	public async Task<SharpIdeFile> MoveFile(SharpIdeFolder destinationParentFolder, SharpIdeFile fileToMove)
	{
		var oldPath = fileToMove.Path;
		var newFilePath = Path.Combine(destinationParentFolder.ChildNodeBasePath, fileToMove.Name.Value);
		var parentFolder = (SharpIdeFolder)fileToMove.Parent;
		parentFolder.Files.Remove(fileToMove);
		var insertionIndex = GetInsertionPosition(destinationParentFolder, fileToMove);
		destinationParentFolder.Files.Insert(insertionIndex, fileToMove);
		fileToMove.Parent = destinationParentFolder;
		fileToMove.Path = newFilePath;
		if (RootFolder.AllFiles.TryRemove(oldPath, out _) is false) _logger.LogWarning("File {filePath} not found in AllFiles when moving file", oldPath);
		if (RootFolder.AllFiles.TryAdd(fileToMove.Path, fileToMove) is false) _logger.LogWarning("File {filePath} already exists in AllFiles when moving file", fileToMove.Path);
		await _fileChangedService.SharpIdeFileMoved(fileToMove, oldPath);
		return fileToMove;
	}

	public async Task<SharpIdeFile> RenameFile(SharpIdeFile fileToRename, string renamedFileName)
	{
		var oldPath = fileToRename.Path;
		var newFilePath = Path.Combine(Path.GetDirectoryName(oldPath)!, renamedFileName);
		fileToRename.Name.Value = renamedFileName;
		fileToRename.Path = newFilePath;
		var parentFolder = (SharpIdeFolder)fileToRename.Parent;
		var currentPosition = parentFolder.Files.IndexOf(fileToRename);
		var insertionPosition = GetMovePosition(parentFolder, fileToRename);
		if (currentPosition != insertionPosition) parentFolder.Files.Move(currentPosition, insertionPosition);
		if (RootFolder.AllFiles.TryRemove(oldPath, out _) is false) _logger.LogWarning("File {filePath} not found in AllFiles when moving file", oldPath);
		if (RootFolder.AllFiles.TryAdd(fileToRename.Path, fileToRename) is false) _logger.LogWarning("File {filePath} already exists in AllFiles when moving file", fileToRename.Path);
		await _fileChangedService.SharpIdeFileRenamed(fileToRename, oldPath);
		return fileToRename;
	}

	private static int GetInsertionPosition(SharpIdeFolder parentFolder, IFileOrFolder fileOrFolder)
	{
		var correctInsertionPosition = fileOrFolder switch
		{
			SharpIdeFile f => parentFolder.Files.list.BinarySearch(f, SharpIdeFileComparer.Instance),
			SharpIdeFolder d => parentFolder.Folders.list.BinarySearch(d, SharpIdeFolderComparer.Instance),
			_ => throw new InvalidOperationException("Unknown file or folder type")
		};
		if (correctInsertionPosition < 0)
		{
			correctInsertionPosition = ~correctInsertionPosition;
		}
		else
		{
			throw new InvalidOperationException("File or folder already exists in the containing folder");
		}
		return correctInsertionPosition;
	}

	private static int GetMovePosition(SharpIdeFolder parentFolder, IFileOrFolder fileOrFolder)
	{
		var correctInsertionPosition = fileOrFolder switch
		{
			SharpIdeFile f => parentFolder.Files.list
				.FindAll(x => x != f) // TODO: Investigate allocations
				.BinarySearch(f, SharpIdeFileComparer.Instance),
			SharpIdeFolder d => parentFolder.Folders.list
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
			throw new InvalidOperationException("File or folder already exists in the containing folder");
		}
		return correctInsertionPosition;
	}
}
