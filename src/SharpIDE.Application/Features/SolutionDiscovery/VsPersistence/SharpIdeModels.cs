using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.CodeAnalysis;
using ObservableCollections;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Events;
using Project = Microsoft.Build.Evaluation.Project;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public interface ISharpIdeNode;

public interface IExpandableSharpIdeNode
{
	public bool Expanded { get; set; }
}
public interface ISolutionOrProject
{
	public string DirectoryPath { get; set; }
}
public interface IFolderOrProject : IExpandableSharpIdeNode, IChildSharpIdeNode
{
	public ObservableList<SharpIdeFolder> Folders { get; init; }
	public ObservableList<SharpIdeFile> Files { get; init; }
	public string Name { get; set; }
	public string ChildNodeBasePath { get; }
}
public interface IFileOrFolder : IChildSharpIdeNode
{
	public string Path { get; set; }
	public string Name { get; set; }
}
public interface IChildSharpIdeNode
{
	public IExpandableSharpIdeNode Parent { get; set; }

	// TODO: Profile/redesign
	public SharpIdeProjectModel? GetNearestProjectNode()
	{
		var current = this;
		while (current is not SharpIdeProjectModel && current?.Parent is not null)
		{
			current = current.Parent as IChildSharpIdeNode;
		}
		return current as SharpIdeProjectModel;
	}
}

public class SharpIdeSolutionModel : ISharpIdeNode, IExpandableSharpIdeNode, ISolutionOrProject
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required string DirectoryPath { get; set; }
	public required ObservableHashSet<SharpIdeProjectModel> Projects { get; set; }
	public required ObservableHashSet<SharpIdeSolutionFolder> SlnFolders { get; set; }
	public required HashSet<SharpIdeProjectModel> AllProjects { get; set; } // TODO: this isn't thread safe
	public required ConcurrentDictionary<string, SharpIdeFile> AllFiles { get; set; }
	public required HashSet<SharpIdeFolder> AllFolders { get; set; } // TODO: this isn't thread safe
	public bool Expanded { get; set; }

	[SetsRequiredMembers]
	internal SharpIdeSolutionModel(string solutionFilePath, IntermediateSolutionModel intermediateModel)
	{
		var solutionName = Path.GetFileName(solutionFilePath);
		var allProjects = new ConcurrentBag<SharpIdeProjectModel>();
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		Name = solutionName;
		FilePath = solutionFilePath;
		DirectoryPath = Path.GetDirectoryName(solutionFilePath)!;
		Projects = new ObservableHashSet<SharpIdeProjectModel>(intermediateModel.Projects.Select(s => new SharpIdeProjectModel(s, allProjects, allFiles, allFolders, this)));
		SlnFolders = new ObservableHashSet<SharpIdeSolutionFolder>(intermediateModel.SolutionFolders.Select(s => new SharpIdeSolutionFolder(s, allProjects, allFiles, allFolders, this)));
		AllProjects = allProjects.ToHashSet();
		AllFiles = new ConcurrentDictionary<string, SharpIdeFile>(allFiles.DistinctBy(s => s.Path).ToDictionary(s => s.Path));
		AllFolders = allFolders.ToHashSet();
	}
}
public class SharpIdeSolutionFolder : ISharpIdeNode, IExpandableSharpIdeNode, IChildSharpIdeNode
{
	public required string Name { get; set; }
	public required ObservableHashSet<SharpIdeSolutionFolder> Folders { get; set; }
	public required ObservableHashSet<SharpIdeProjectModel> Projects { get; set; }
	public required ObservableHashSet<SharpIdeFile> Files { get; set; }
	public bool Expanded { get; set; }
	public required IExpandableSharpIdeNode Parent { get; set; }

	[SetsRequiredMembers]
	internal SharpIdeSolutionFolder(IntermediateSlnFolderModel intermediateModel, ConcurrentBag<SharpIdeProjectModel> allProjects, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders, IExpandableSharpIdeNode parent)
	{
		Name = intermediateModel.Model.Name;
		Parent = parent;
		Files = new ObservableHashSet<SharpIdeFile>(intermediateModel.Files.Select(s => new SharpIdeFile(s.FullPath, s.Name, s.Extension, this, allFiles)));
		Folders = new ObservableHashSet<SharpIdeSolutionFolder>(intermediateModel.Folders.Select(x => new SharpIdeSolutionFolder(x, allProjects, allFiles, allFolders, this)));
		Projects = new ObservableHashSet<SharpIdeProjectModel>(intermediateModel.Projects.Select(x => new SharpIdeProjectModel(x, allProjects, allFiles, allFolders, this)));
	}
}
public class SharpIdeProjectModel : ISharpIdeNode, IExpandableSharpIdeNode, IChildSharpIdeNode, IFolderOrProject, ISolutionOrProject
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required string DirectoryPath { get; set; }
	public string ChildNodeBasePath => DirectoryPath;
	public required ObservableList<SharpIdeFolder> Folders { get; init; }
	public required ObservableList<SharpIdeFile> Files { get; init; }
	public bool Expanded { get; set; }
	public required IExpandableSharpIdeNode Parent { get; set; }
	public bool Running { get; set; }
	public CancellationTokenSource? RunningCancellationTokenSource { get; set; }
	public required Task<Project> MsBuildEvaluationProjectTask { get; set; }

	[SetsRequiredMembers]
	internal SharpIdeProjectModel(IntermediateProjectModel projectModel, ConcurrentBag<SharpIdeProjectModel> allProjects, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders, IExpandableSharpIdeNode parent)
	{
		Parent = parent;
		Name = projectModel.Model.ActualDisplayName;
		FilePath = projectModel.FullFilePath;
		DirectoryPath = Path.GetDirectoryName(projectModel.FullFilePath)!;
		Files = new ObservableList<SharpIdeFile>(TreeMapperV2.GetFiles(projectModel.FullFilePath, this, allFiles));
		Folders = new ObservableList<SharpIdeFolder>(TreeMapperV2.GetSubFolders(projectModel.FullFilePath, this, allFiles, allFolders));
		MsBuildEvaluationProjectTask = Task.Run(() => ProjectEvaluation.GetProject(projectModel.FullFilePath));
		allProjects.Add(this);
	}

	public Project MsBuildEvaluationProject => MsBuildEvaluationProjectTask.IsCompletedSuccessfully
		? MsBuildEvaluationProjectTask.Result
		: throw new InvalidOperationException("Do not attempt to access the MsBuildEvaluationProject before it has been loaded");

	public bool IsRunnable => MsBuildEvaluationProject.GetPropertyValue("OutputType") is "Exe" or "WinExe" || IsBlazorProject || IsGodotProject;
	public bool IsBlazorProject => MsBuildEvaluationProject.Xml.Sdk is "Microsoft.NET.Sdk.BlazorWebAssembly";
	public bool IsGodotProject => MsBuildEvaluationProject.Xml.Sdk.StartsWith("Godot.NET.Sdk");
	public bool IsMtpTestProject => MsBuildEvaluationProject.GetPropertyValue("IsTestingPlatformApplication") is "true";
	public string BlazorDevServerVersion => MsBuildEvaluationProject.Items.Single(s => s.ItemType is "PackageReference" && s.EvaluatedInclude is "Microsoft.AspNetCore.Components.WebAssembly.DevServer").GetMetadataValue("Version");
	public bool OpenInRunPanel { get; set; }
	public Channel<byte[]>? RunningOutputChannel { get; set; }

	public EventWrapper<Task> ProjectRunFailed { get; } = new(() => Task.CompletedTask);
	public EventWrapper<Task> ProjectStartedRunning { get; } = new(() => Task.CompletedTask);
	public EventWrapper<Task> ProjectStoppedRunning { get; } = new(() => Task.CompletedTask);


	public ObservableHashSet<SharpIdeDiagnostic> Diagnostics { get; internal set; } = [];
}
