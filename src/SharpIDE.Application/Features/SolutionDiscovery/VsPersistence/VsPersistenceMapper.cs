using System.Diagnostics;
using SharpIDE.Application.Features.ProjectIntrospection;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public static class VsPersistenceMapper
{
	public static async Task<SharpIdeSolutionModel> GetSolutionModel(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		var timer = Stopwatch.StartNew();
		// This intermediate model is pretty much useless, but I have left it around as we grab the project nodes with it, which we might use later.
		var intermediateModel = await IntermediateMapper.GetIntermediateModel(solutionFilePath, cancellationToken);

		var solutionName = Path.GetFileName(solutionFilePath);
		var allProjects = new List<SharpIdeProjectModel>();
		var solutionModel = new SharpIdeSolutionModel
		{
			Name = solutionName,
			FilePath = solutionFilePath,
			Projects = intermediateModel.Projects.Select(s => GetSharpIdeProjectModel(s, allProjects)).ToList(),
			AllProjects = allProjects,
			Folders = intermediateModel.SolutionFolders.Select(s => new SharpIdeSolutionFolder
			{
				Name = s.Model.Name,
				Files = s.Files.Select(GetSharpIdeFile).ToList(),
				Folders = s.Folders.Select(x => GetSharpIdeSolutionFolder(x, allProjects)).ToList(),
				Projects = s.Projects.Select(x => GetSharpIdeProjectModel(x, allProjects)).ToList()
			}).ToList(),
		};

		timer.Stop();
		Console.WriteLine($"Solution model fully created in {timer.ElapsedMilliseconds} ms");

		return solutionModel;
	}
	private static SharpIdeProjectModel GetSharpIdeProjectModel(IntermediateProjectModel projectModel, List<SharpIdeProjectModel> allProjects)
	{
		var project = new SharpIdeProjectModel
		{
			Name = projectModel.Model.ActualDisplayName,
			FilePath = projectModel.FullFilePath,
			Files = TreeMapperV2.GetFiles(projectModel.FullFilePath),
			Folders = TreeMapperV2.GetSubFolders(projectModel.FullFilePath),
			MsBuildEvaluationProject = Test.GetProject(projectModel.FullFilePath)
		};
		allProjects.Add(project);
		return project;
	}

	private static SharpIdeSolutionFolder GetSharpIdeSolutionFolder(IntermediateSlnFolderModel folderModel, List<SharpIdeProjectModel> allProjects) => new SharpIdeSolutionFolder()
	{
		Name = folderModel.Model.Name,
		Files = folderModel.Files.Select(GetSharpIdeFile).ToList(),
		Folders = folderModel.Folders.Select(s => GetSharpIdeSolutionFolder(s, allProjects)).ToList(),
		Projects = folderModel.Projects.Select(s => GetSharpIdeProjectModel(s, allProjects)).ToList()
	};

	private static SharpIdeFile GetSharpIdeFile(IntermediateSlnFolderFileModel fileModel) => new SharpIdeFile
	{
		Path = fileModel.FullPath,
		Name = fileModel.Name
	};
}
