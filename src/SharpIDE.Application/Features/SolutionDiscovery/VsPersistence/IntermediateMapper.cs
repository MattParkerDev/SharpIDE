using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public static class IntermediateMapper
{
	internal static async Task<IntermediateSolutionModel> GetIntermediateModel(string solutionFilePath,
		CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity();
		var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
		Guard.Against.Null(serializer, nameof(serializer));
		var vsSolution = await serializer.OpenAsync(solutionFilePath, cancellationToken);
		
		// Remove any projects that aren't csproj, TODO: Instead of removing, display in the solution explorer that the project type isn't supported
		foreach (var vsSolutionSolutionProject in vsSolution.SolutionProjects.Where(s => s.Extension is not ".csproj").ToList())
		{
			vsSolution.RemoveProject(vsSolutionSolutionProject);
		}

		var rootFolders = vsSolution.SolutionFolders
			.Where(f => f.Parent is null)
			.Select(f => GetSlnFolderModel(f, solutionFilePath, vsSolution.SolutionFolders, vsSolution.SolutionProjects))
			.OrderBy(s => s.Model.Name)
			.ToList();

		var rootProjects = vsSolution.SolutionProjects
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
			.Select(f => new IntermediateSlnFolderFileModel
			{
				Name = Path.GetFileName(f),
				FullPath = new FileInfo(Path.Join(Path.GetDirectoryName(solutionFilePath), f)).FullName
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
