using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileOperationsService(SharpIdeSolutionModificationService sharpIdeSolutionModificationService)
{
	private readonly SharpIdeSolutionModificationService _sharpIdeSolutionModificationService = sharpIdeSolutionModificationService;

	public async Task RenameDirectory(SharpIdeFolder folder, string newDirectoryName)
	{
		var parentPath = Path.GetDirectoryName(folder.Path)!;
		var newDirectoryPath = Path.Combine(parentPath, newDirectoryName);
		Directory.Move(folder.Path, newDirectoryPath);
		await _sharpIdeSolutionModificationService.RenameDirectory(folder, newDirectoryName);
	}

	public async Task CreateDirectory(IFolderOrProject parentNode, string newDirectoryName)
	{
		var newDirectoryPath = Path.Combine(parentNode.ChildNodeBasePath, newDirectoryName);
		Directory.CreateDirectory(newDirectoryPath);
		var newFolder = await _sharpIdeSolutionModificationService.AddDirectory(parentNode, newDirectoryName);
	}

	public async Task DeleteDirectory(SharpIdeFolder folder)
	{
		Directory.Delete(folder.Path, true);
		await _sharpIdeSolutionModificationService.RemoveDirectory(folder);
	}

	public async Task CopyDirectory(IFolderOrProject destinationParentNode, string sourceDirectoryPath, string newDirectoryName)
	{
		var newDirectoryPath = Path.Combine(destinationParentNode.ChildNodeBasePath, newDirectoryName);
		CopyAll(new DirectoryInfo(sourceDirectoryPath), new DirectoryInfo(newDirectoryPath));
		var newFolder = await _sharpIdeSolutionModificationService.AddDirectory(destinationParentNode, newDirectoryName);
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

	public async Task MoveDirectory(IFolderOrProject destinationParentNode, SharpIdeFolder folderToMove)
	{
		var newDirectoryPath = Path.Combine(destinationParentNode.ChildNodeBasePath, folderToMove.Name);
		Directory.Move(folderToMove.Path, newDirectoryPath);
		await _sharpIdeSolutionModificationService.MoveDirectory(destinationParentNode, folderToMove);
	}

	public async Task DeleteFile(SharpIdeFile file)
	{
		File.Delete(file.Path);
		await _sharpIdeSolutionModificationService.RemoveFile(file);
	}

	public async Task<SharpIdeFile> CreateCsFile(IFolderOrProject parentNode, string newFileName, string typeKeyword)
	{
		var newFilePath = Path.Combine(GetFileParentNodePath(parentNode), newFileName);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		var className = Path.GetFileNameWithoutExtension(newFileName);
		var @namespace = NewFileTemplates.ComputeNamespace(parentNode);
		var fileText = NewFileTemplates.CsharpFile(className, @namespace, typeKeyword);
		await File.WriteAllTextAsync(newFilePath, fileText);
		var sharpIdeFile = await _sharpIdeSolutionModificationService.CreateFile(parentNode, newFilePath, newFileName, fileText);
		return sharpIdeFile;
	}

	public async Task<SharpIdeFile> CopyFile(IFolderOrProject destinationParentNode, string sourceFilePath, string newFileName)
	{
		var newFilePath = Path.Combine(GetFileParentNodePath(destinationParentNode), newFileName);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		var fileContents = await File.ReadAllTextAsync(sourceFilePath);
		File.Copy(sourceFilePath, newFilePath);
		var sharpIdeFile = await _sharpIdeSolutionModificationService.CreateFile(destinationParentNode, newFilePath, newFileName, fileContents);
		return sharpIdeFile;
	}

	public async Task<SharpIdeFile> RenameFile(SharpIdeFile file, string newFileName)
	{
		var parentPath = Path.GetDirectoryName(file.Path)!;
		var newFilePath = Path.Combine(parentPath, newFileName);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		File.Move(file.Path, newFilePath);
		var sharpIdeFile = await _sharpIdeSolutionModificationService.RenameFile(file, newFileName);
		return sharpIdeFile;
	}

	public async Task<SharpIdeFile> MoveFile(IFolderOrProject destinationParentNode, SharpIdeFile fileToMove)
	{
		var newFilePath = Path.Combine(destinationParentNode.ChildNodeBasePath, fileToMove.Name);
		if (File.Exists(newFilePath)) throw new InvalidOperationException($"File {newFilePath} already exists.");
		File.Move(fileToMove.Path, newFilePath);
		var sharpIdeFile = await _sharpIdeSolutionModificationService.MoveFile(destinationParentNode, fileToMove);
		return sharpIdeFile;
	}

	private static string GetFileParentNodePath(IFolderOrProject parentNode) => parentNode switch
	{
		SharpIdeFolder folder => folder.Path,
		SharpIdeProjectModel project => Path.GetDirectoryName(project.FilePath)!,
		_ => throw new InvalidOperationException("Parent node must be a folder or project")
	};
}
