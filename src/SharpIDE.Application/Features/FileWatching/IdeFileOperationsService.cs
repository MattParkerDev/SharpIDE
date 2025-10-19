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
}
