using System.Collections.Concurrent;
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

	public required SharpIdeRootFolder RootFolder { get; set; }
	public ConcurrentDictionary<string, SharpIdeFile> AllFiles => RootFolder.AllFiles;
	public ObservableList<SharpIdeFolder> AllFolders => RootFolder.AllFolders;

	[SetsRequiredMembers]
	internal SharpIdeSolutionModel(string solutionFilePath, IntermediateSolutionModel intermediateModel, SharpIdeRootFolder sharpIdeRootFolder)
	{
		var solutionName = Path.GetFileName(solutionFilePath);
		var allProjects = new ConcurrentBag<SharpIdeProjectModel>();
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		Name = solutionName;
		FilePath = solutionFilePath;
		DirectoryPath = Path.GetDirectoryName(solutionFilePath)!;
		RootFolder = sharpIdeRootFolder;
		Projects = new ObservableList<SharpIdeProjectModel>(intermediateModel.Projects.Select(s => new SharpIdeProjectModel(s, allProjects, allFiles, allFolders, this, sharpIdeRootFolder)));
		SlnFolders = new ObservableList<SharpIdeSolutionFolder>(intermediateModel.SolutionFolders.Select(s => new SharpIdeSolutionFolder(s, allProjects, allFiles, allFolders, this, sharpIdeRootFolder)));
		AllProjects = new ObservableHashSet<SharpIdeProjectModel>(allProjects);
	}
}
