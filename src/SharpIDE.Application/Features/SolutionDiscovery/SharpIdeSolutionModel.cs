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
		RebuildProjectLookups();
	}

	private sealed record ProjectLookups(FrozenDictionary<string, SharpIdeProjectModel> ByDirectory, FrozenDictionary<string, SharpIdeProjectModel> ByFilePath);
	/// Swapped as a whole, so readers never see the two dictionaries out of step
	private volatile ProjectLookups _projectLookups = new(FrozenDictionary<string, SharpIdeProjectModel>.Empty, FrozenDictionary<string, SharpIdeProjectModel>.Empty);

	/// Must be called whenever AllProjects changes
	internal void RebuildProjectLookups()
	{
		var byDirectory = new Dictionary<string, SharpIdeProjectModel>(PathComparison.Comparer);
		var byFilePath = new Dictionary<string, SharpIdeProjectModel>(PathComparison.Comparer);
		// .ToList to avoid 'Object synchronization method was called from an unsynchronized block of code'
		foreach (var project in AllProjects.ToList())
		{
			byDirectory.TryAdd(project.DirectoryPath, project); // First wins, as two csproj files may share a folder
			byFilePath.TryAdd(project.FilePath, project);
		}
		_projectLookups = new ProjectLookups(byDirectory.ToFrozenDictionary(PathComparison.Comparer), byFilePath.ToFrozenDictionary(PathComparison.Comparer));
	}

	public SharpIdeProjectModel? GetProjectByFilePath(string? projectFilePath) => projectFilePath is null ? null : _projectLookups.ByFilePath.GetValueOrDefault(projectFilePath);

	/// The project whose folder most closely contains the file, or null if none does (e.g. a solution-level .editorconfig).
	/// Only a guess - it misses files linked in from elsewhere, and includes files the project's globs exclude. For a file already in the workspace,
	/// RoslynAnalysis resolves its actual project(s) from the workspace instead.
	public SharpIdeProjectModel? GetProjectByContainingFolder(SharpIdeFile file)
	{
		var byDirectory = _projectLookups.ByDirectory;
		var current = file.Parent;
		while (current is SharpIdeFolder folder)
		{
			if (byDirectory.TryGetValue(folder.Path, out var project)) return project;
			current = folder.Parent;
		}
		return null;
	}
}
