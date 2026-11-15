using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

internal class IntermediateSolutionModel
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required List<IntermediateProjectModel> Projects { get; set; }
	public required List<IntermediateSlnFolderModel> SolutionFolders { get; set; }
}

internal class IntermediateSlnFolderModel
{
	public required SolutionFolderModel Model { get; set; }
	public required List<IntermediateSlnFolderModel> Folders { get; set; }
	public required List<IntermediateProjectModel> Projects { get; set; }
	public required List<IntermediateSlnFolderFileModel> Files { get; set; }
}

internal class IntermediateProjectModel
{
	public required SolutionProjectModel Model { get; set; }
	public required string FullFilePath { get; set; }
	public required Guid Id { get; set; }
}

internal class IntermediateSlnFolderFileModel
{
	public required string Name { get; set; }
	public required string FullPath { get; set; }
	public required string Extension { get; set; }
}
