using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileOperationsService(SharpIdeSolutionModificationService sharpIdeSolutionModificationService)
{
	private readonly SharpIdeSolutionModificationService _sharpIdeSolutionModificationService = sharpIdeSolutionModificationService;

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

	public async Task DeleteFile(SharpIdeFile file)
	{
		File.Delete(file.Path);
		await _sharpIdeSolutionModificationService.RemoveFile(file);
	}

	public async Task CreateCsFile(IFolderOrProject parentNode, string newFileName)
	{
		var newFilePath = Path.Combine(GetFileParentNodePath(parentNode), newFileName);
		var className = Path.GetFileNameWithoutExtension(newFileName);
		var @namespace = NewFileTemplates.ComputeNamespace(parentNode);
		var fileText = NewFileTemplates.CsharpClass(className, @namespace);
		await File.WriteAllTextAsync(newFilePath, fileText);
		await _sharpIdeSolutionModificationService.CreateFile(parentNode, newFilePath, newFileName, fileText);
	}

	private static string GetFileParentNodePath(IFolderOrProject parentNode) => parentNode switch
	{
		SharpIdeFolder folder => folder.Path,
		SharpIdeProjectModel project => Path.GetDirectoryName(project.FilePath)!,
		_ => throw new InvalidOperationException("Parent node must be a folder or project")
	};
}
