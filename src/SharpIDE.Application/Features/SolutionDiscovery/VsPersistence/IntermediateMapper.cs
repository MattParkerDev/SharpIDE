using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public static class IntermediateMapper
{
	internal static async Task<IntermediateSolutionModel> GetIntermediateModel(string solutionFilePath, SolutionModel vsSolution, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity();

		// Skip any projects that aren't csproj, TODO: Instead of skipping, display in the solution explorer that the project type isn't supported
		// Filtered, not removed from vsSolution - it is saved back to disk when the IDE edits the solution, which would delete them from the user's solution file
		var csprojProjects = vsSolution.SolutionProjects.Where(s => s.Extension is ".csproj").ToList();

		var rootFolders = vsSolution.SolutionFolders
			.Where(f => f.Parent is null)
			.Select(f => GetSlnFolderModel(f, solutionFilePath, vsSolution.SolutionFolders, csprojProjects))
			.OrderBy(s => s.Model.Name)
			.ToList();

		var rootProjects = csprojProjects
			.Where(p => p.Parent is null)
			.Select(s => s.GetProjectModel(solutionFilePath))
			.OrderBy(s => s.Model.ActualDisplayName)
			.ToList();

		var solutionModel = new IntermediateSolutionModel
		{
			Name = Path.GetFileName(solutionFilePath),
			FilePath = solutionFilePath,
			Projects = rootProjects,
			SolutionFolders = rootFolders
		};
		return solutionModel;
	}

	private static IntermediateSlnFolderModel GetSlnFolderModel(SolutionFolderModel folder, string solutionFilePath,
		IReadOnlyList<SolutionFolderModel> allSolutionFolders, IReadOnlyList<SolutionProjectModel> allSolutionProjects)
	{
		var childFolders = allSolutionFolders
			.Where(f => f.Parent == folder)
			.Select(f => GetSlnFolderModel(f, solutionFilePath, allSolutionFolders, allSolutionProjects))
			.OrderBy(s => s.Model.Name)
			.ToList();

		var projectsInFolder = allSolutionProjects
			.Where(p => p.Parent == folder)
			.Select(s => s.GetProjectModel(solutionFilePath))
			.OrderBy(s => s.Model.ActualDisplayName)
			.ToList();

		var filesInFolder = folder.Files?
			.Select(f =>
			{
				var fileInfo = new FileInfo(Path.Join(Path.GetDirectoryName(solutionFilePath), f));
				return new IntermediateSlnFolderFileModel
				{
					Name = Path.GetFileName(f),
					FullPath = fileInfo.FullName,
					Extension = fileInfo.Extension
				};
			})
			.ToList() ?? [];

		return new IntermediateSlnFolderModel
		{
			Model = folder,
			Folders = childFolders,
			Projects = projectsInFolder,
			Files = filesInFolder
		};
	}

	private static IntermediateProjectModel GetProjectModel(this SolutionProjectModel project, string solutionFilePath)
	{
		return new IntermediateProjectModel
		{
			Model = project,
			Id = project.Id,
			FullFilePath = new DirectoryInfo(Path.Join(Path.GetDirectoryName(solutionFilePath), project.FilePath)).FullName
		};
	}
}
