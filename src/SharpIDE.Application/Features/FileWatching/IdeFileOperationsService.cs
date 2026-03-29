using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileOperationsService(SharpIdeRootFolderModificationService rootFolderModificationService, IdeFileExternalChangeHandler ideFileExternalChangeHandler)
{
	private readonly SharpIdeRootFolderModificationService _rootFolderModificationService = rootFolderModificationService;
	private readonly IdeFileExternalChangeHandler _ideFileExternalChangeHandler = ideFileExternalChangeHandler;

	public async Task RenameDirectory(SharpIdeFolder folder, string newDirectoryName)
	{
		var parentPath = Path.GetDirectoryName(folder.Path)!;
		var newDirectoryPath = Path.Combine(parentPath, newDirectoryName);
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		Directory.Move(folder.Path, newDirectoryPath);
		await _rootFolderModificationService.RenameDirectory(folder, newDirectoryName);
	}

	public async Task CreateDirectory(SharpIdeFolder parentFolder, string newDirectoryName)
	{
		var newDirectoryPath = Path.Combine(parentFolder.ChildNodeBasePath, newDirectoryName);
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		Directory.CreateDirectory(newDirectoryPath);
		await _rootFolderModificationService.AddDirectory(parentFolder, newDirectoryName);
	}

	public async Task DeleteDirectory(SharpIdeFolder folder)
	{
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		Directory.Delete(folder.Path, true);
		await _rootFolderModificationService.RemoveDirectory(folder);
	}

	public async Task CopyDirectory(SharpIdeFolder destinationParentFolder, string sourceDirectoryPath, string newDirectoryName)
	{
		var newDirectoryPath = Path.Combine(destinationParentFolder.ChildNodeBasePath, newDirectoryName);
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		CopyAll(new DirectoryInfo(sourceDirectoryPath), new DirectoryInfo(newDirectoryPath));
		await _rootFolderModificationService.AddDirectory(destinationParentFolder, newDirectoryName);
		return;

		static void CopyAll(DirectoryInfo source, DirectoryInfo target)
		{
			Directory.CreateDirectory(target.FullName);
			foreach (var fi in source.GetFiles())
			{
				fi.CopyTo(Path.Combine(target.FullName, fi.Name));
			}

			foreach (var diSourceSubDir in source.GetDirectories())
			{
				var nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
				CopyAll(diSourceSubDir, nextTargetSubDir);
			}
		}
	}

	public async Task MoveDirectory(SharpIdeFolder destinationParentFolder, SharpIdeFolder folderToMove)
	{
		var newDirectoryPath = Path.Combine(destinationParentFolder.ChildNodeBasePath, folderToMove.Name.Value);
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		Directory.Move(folderToMove.Path, newDirectoryPath);
		await _rootFolderModificationService.MoveDirectory(destinationParentFolder, folderToMove);
	}

	public async Task DeleteFile(SharpIdeFile file)
	{
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		File.Delete(file.Path);
		await _rootFolderModificationService.RemoveFile(file);
	}

	public async Task<SharpIdeFile> CreateCsFile(SharpIdeFolder parentFolder, string newFileName, string typeKeyword)
	{
		var newFilePath = Path.Combine(parentFolder.Path, newFileName);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		var className = Path.GetFileNameWithoutExtension(newFileName);
		var @namespace = NewFileTemplates.ComputeNamespace(parentFolder);
		var fileText = NewFileTemplates.CsharpFile(className, @namespace, typeKeyword);
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		await File.WriteAllTextAsync(newFilePath, fileText);
		var sharpIdeFile = await _rootFolderModificationService.CreateFile(parentFolder, newFilePath, newFileName, fileText);
		return sharpIdeFile;
	}

	public async Task<SharpIdeFile> CopyFile(SharpIdeFolder destinationParentFolder, string sourceFilePath, string newFileName)
	{
		var newFilePath = Path.Combine(destinationParentFolder.Path, newFileName);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		var fileContents = await File.ReadAllTextAsync(sourceFilePath);
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		File.Copy(sourceFilePath, newFilePath);
		var sharpIdeFile = await _rootFolderModificationService.CreateFile(destinationParentFolder, newFilePath, newFileName, fileContents);
		return sharpIdeFile;
	}

	public async Task<SharpIdeFile> RenameFile(SharpIdeFile file, string newFileName)
	{
		var parentPath = Path.GetDirectoryName(file.Path)!;
		var newFilePath = Path.Combine(parentPath, newFileName);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		File.Move(file.Path, newFilePath);
		var sharpIdeFile = await _rootFolderModificationService.RenameFile(file, newFileName);
		return sharpIdeFile;
	}

	public async Task<SharpIdeFile> MoveFile(SharpIdeFolder destinationParentFolder, SharpIdeFile fileToMove)
	{
		var newFilePath = Path.Combine(destinationParentFolder.ChildNodeBasePath, fileToMove.Name.Value);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		using var _ = await _ideFileExternalChangeHandler.IdeChangeLock.LockAsync();
		File.Move(fileToMove.Path, newFilePath);
		var sharpIdeFile = await _rootFolderModificationService.MoveFile(destinationParentFolder, fileToMove);
		return sharpIdeFile;
	}
}
