using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Globbing;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public static class GetNodesInSolution
{
	private static readonly ProjectCollection _projectCollection = new();
	public static SolutionFile? ParseSolutionFileFromPath(string solutionFilePath)
	{
		var solutionFile = SolutionFile.Parse(solutionFilePath);
		return solutionFile;
	}

	public static List<ProjectRootElement> GetCSharpProjectObjectsFromSolutionFile(SolutionFile solutionFile)
	{
		var projectList = solutionFile
			.ProjectsByGuid.Where(x => x.Value.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
			.Select(s => ProjectRootElement.Open(s.Value.AbsolutePath))
			.ToList();

		return projectList;
	}

	public static List<FileInfo> GetFilesInProject(string projectPath)
	{
		var project = _projectCollection.LoadProject(projectPath);
		var compositeGlob = new CompositeGlob(project.GetAllGlobs().Select(s => s.MsBuildGlob));
		var directory  = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
		var files = directory.EnumerateFiles("*", SearchOption.AllDirectories)
			.Where(f =>
			{
				var relativeDirectory = Path.GetRelativePath(directory.FullName, f.FullName);
				return compositeGlob.IsMatch(relativeDirectory);
			})
			.ToList();
		return files;
	}

	public static List<Folder> GetFoldersInProject(string projectPath)
	{
		var files = GetFilesInProject(projectPath);
		var rootDirectoryOfProject = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);

		var grouped = files.GroupBy(s => s.Directory!.FullName);
		var folders = grouped.Select(s => new Folder
		{
			Name = Path.GetFileName(s.Key),
			FullName = s.Key,
			ParentFolder = null,
			Files = s.Select(f => new MyFile
			{
				Name = f.Name
			}).ToList()
		}).ToList();

		foreach (var folder in folders)
		{
			var directoryInfo = new DirectoryInfo(folder.FullName);
			if (directoryInfo.FullName == rootDirectoryOfProject.FullName) continue;

			var parent = directoryInfo.Parent;
			try
			{
				var parentFolder = folders.SingleOrDefault(f => f.FullName == parent!.FullName);
				folder.ParentFolder = parentFolder;
			}
			catch (Exception e)
			{
				Console.WriteLine(e);
			}
		}

		return folders;
	}
}




















