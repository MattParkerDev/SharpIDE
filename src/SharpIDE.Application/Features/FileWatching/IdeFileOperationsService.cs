using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileOperationsService(SharpIdeSolutionModificationService sharpIdeSolutionModificationService)
{
	private readonly SharpIdeSolutionModificationService _sharpIdeSolutionModificationService = sharpIdeSolutionModificationService;

	public async Task CreateDirectory(SharpIdeFolder parentFolder, string newDirectoryName)
	{
		var newDirectoryPath = Path.Combine(parentFolder.Path, newDirectoryName);
		Directory.CreateDirectory(newDirectoryPath);
		var newFolder = await _sharpIdeSolutionModificationService.AddDirectory(parentFolder, newDirectoryName);
	}

	public async Task DeleteDirectory(SharpIdeFolder folder)
	{
		Directory.Delete(folder.Path, true);
		await _sharpIdeSolutionModificationService.RemoveDirectory(folder);
	}

	// public async Task DeleteFile(SharpIdeFile file)
	// {
	// 	File.Delete(file.Path);
	// 	await _sharpIdeSolutionModificationService.RemoveFile(file);
	// }

	public async Task CreateCsFile(SharpIdeFolder parentFolder, string newFileName, string @namespace)
	{
		var newFilePath = Path.Combine(parentFolder.Path, newFileName);
		var className = Path.GetFileNameWithoutExtension(newFileName);
		var fileText = NewFileTemplates.CsharpClass(className, @namespace);
		await File.WriteAllTextAsync(newFilePath, fileText);
		await _sharpIdeSolutionModificationService.CreateFile(parentFolder, newFileName, fileText);
	}
}
