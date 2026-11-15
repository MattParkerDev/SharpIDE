using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using ObservableCollections;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeSolutionModel : ISharpIdeNode, IExpandableSharpIdeNode, ISolutionOrProject, ISolutionOrSolutionFolder
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required string DirectoryPath { get; set; }
	public required ObservableList<SharpIdeProjectModel> Projects { get; set; }
	public required ObservableList<SharpIdeSolutionFolder> SlnFolders { get; set; }
	public required ObservableHashSet<SharpIdeProjectModel> AllProjects { get; set; }
	public bool Expanded { get; set; }

	public required SharpIdeFileTree FileTree { get; set; }
	public ConcurrentDictionary<string, SharpIdeFile> AllFiles => FileTree.AllFiles;
	public ObservableList<SharpIdeFolder> AllFolders => FileTree.AllFolders;

	[SetsRequiredMembers]
	internal SharpIdeSolutionModel(string solutionFilePath, IntermediateSolutionModel intermediateModel, SharpIdeFileTree fileTree)
	{
		var solutionName = Path.GetFileName(solutionFilePath);
		var allProjects = new ConcurrentBag<SharpIdeProjectModel>();
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		Name = solutionName;
		FilePath = solutionFilePath;
		DirectoryPath = Path.GetDirectoryName(solutionFilePath)!;
		FileTree = fileTree;
		Projects = new ObservableList<SharpIdeProjectModel>(intermediateModel.Projects.Select(s => new SharpIdeProjectModel(s, allProjects, allFiles, allFolders, this, fileTree)));
		SlnFolders = new ObservableList<SharpIdeSolutionFolder>(intermediateModel.SolutionFolders.Select(s => new SharpIdeSolutionFolder(s, allProjects, allFiles, allFolders, this, fileTree)));
		AllProjects = new ObservableHashSet<SharpIdeProjectModel>(allProjects);
		RebuildProjectsByDirectory();
	}

	/// Project directory path -> project, so a file's project is found with a few hash lookups rather than scanning folders and projects
	private volatile FrozenDictionary<string, SharpIdeProjectModel> _projectsByDirectory = FrozenDictionary<string, SharpIdeProjectModel>.Empty;

	/// Must be called whenever AllProjects changes
	internal void RebuildProjectsByDirectory()
	{
		var projectsByDirectory = new Dictionary<string, SharpIdeProjectModel>(PathComparison.Comparer);
		// TryAdd - first wins, as two csproj files may share a folder
		// .ToList to avoid 'Object synchronization method was called from an unsynchronized block of code'
		foreach (var project in AllProjects.ToList()) projectsByDirectory.TryAdd(project.DirectoryPath, project);
		_projectsByDirectory = projectsByDirectory.ToFrozenDictionary(PathComparison.Comparer);
	}

	/// The project whose folder most closely contains the file, or null if the file isn't in a project (e.g. a solution-level .editorconfig)
	public SharpIdeProjectModel? GetProjectForFile(SharpIdeFile file)
	{
		var projectsByDirectory = _projectsByDirectory;
		var current = file.Parent;
		while (current is SharpIdeFolder folder)
		{
			if (projectsByDirectory.TryGetValue(folder.Path, out var project)) return project;
			current = folder.Parent;
		}
		return null;
	}
}
