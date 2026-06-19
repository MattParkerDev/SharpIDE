using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Threading.Channels;
using Ardalis.GuardClauses;
using Microsoft.Build.Evaluation;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeProjectModel : ISharpIdeNode, IExpandableSharpIdeNode, IChildSharpIdeNode, ISolutionOrProject
{
	public required ReactiveProperty<string> Name { get; set; }
	public required string FilePath { get; set; }
	public required string DirectoryPath { get; set; }
	/// The folder on disk that contains this project's .csproj file and all its source files.
	public required SharpIdeFolder? Folder { get; set; }
	public bool Expanded { get; set; }
	public required IExpandableSharpIdeNode Parent { get; set; }
	public bool Running { get; set; }
	public CancellationTokenSource? RunningCancellationTokenSource { get; set; }
	public ReactiveProperty<MsBuildProjectLoadState> MsBuildProjectLoadState { get; set; }
	public required Task<Project> MsBuildEvaluationProjectTask { get; set; }

	[SetsRequiredMembers]
	internal SharpIdeProjectModel(IntermediateProjectModel projectModel, ConcurrentBag<SharpIdeProjectModel> allProjects, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders, IExpandableSharpIdeNode parent, SharpIdeRootFolder sharpIdeRootFolder)
	{
		Parent = parent;
		Name = new ReactiveProperty<string>(projectModel.Model.ActualDisplayName);
		FilePath = projectModel.FullFilePath;
		DirectoryPath = Path.GetDirectoryName(projectModel.FullFilePath)!;
		Folder = sharpIdeRootFolder.GetFolderForProject(projectModel.FullFilePath);
		MsBuildProjectLoadState = new ReactiveProperty<MsBuildProjectLoadState>(Evaluation.MsBuildProjectLoadState.Loading);
		MsBuildEvaluationProjectTask = LoadOrReloadProjectInMsBuild();
		allProjects.Add(this);
	}

	public async Task<Project> LoadOrReloadProjectInMsBuild()
	{
		return await Task.Run(async () =>
		{
			if (Folder is null)
			{
				MsBuildProjectLoadState.Value = Evaluation.MsBuildProjectLoadState.Missing;
				return null!;
			}
			var result = await ProjectEvaluation.LoadOrReloadProject(FilePath);
			MsBuildProjectLoadState.Value = result.LoadState;
			Diagnostics.RemoveRange(Diagnostics.set); // Clear regardless
			if (result.LoadState is Evaluation.MsBuildProjectLoadState.Invalid)
			{
				Guard.Against.Null(result.Diagnostic);
				Diagnostics.Add(result.Diagnostic);
			}
			return result.Project!;
		});
	}

	public Project MsBuildEvaluationProject => MsBuildEvaluationProjectTask.IsCompletedSuccessfully ? MsBuildEvaluationProjectTask.Result : throw new InvalidOperationException("Do not attempt to access the MsBuildEvaluationProject before it has been loaded");

	public bool IsLoading => MsBuildProjectLoadState.Value is Evaluation.MsBuildProjectLoadState.Loading;
	public bool IsLoaded => MsBuildProjectLoadState.Value is Evaluation.MsBuildProjectLoadState.Loaded;
	public bool IsInvalid => MsBuildProjectLoadState.Value is Evaluation.MsBuildProjectLoadState.Invalid or Evaluation.MsBuildProjectLoadState.Missing;
	public bool IsRunnable => MsBuildEvaluationProject.GetPropertyValue("OutputType") is "Exe" or "WinExe" || IsBlazorProject || IsGodotProject;
	public bool IsBlazorProject => MsBuildEvaluationProject.Xml.Sdk is "Microsoft.NET.Sdk.BlazorWebAssembly";
	public bool IsGodotProject => MsBuildEvaluationProject.Xml.Sdk.StartsWith("Godot.NET.Sdk");
	public bool IsMtpTestProject => MsBuildEvaluationProject.GetPropertyValue("IsTestingPlatformApplication") is "true";
	public string RootNamespace
	{
		get
		{
			var rootNamespace = MsBuildEvaluationProject.GetPropertyValue("RootNamespace");
			return string.IsNullOrWhiteSpace(rootNamespace) ? Name.Value : rootNamespace;
		}
	}
	public string BlazorDevServerVersion => MsBuildEvaluationProject.Items.Single(s => s.ItemType is "PackageReference" && s.EvaluatedInclude is "Microsoft.AspNetCore.Components.WebAssembly.DevServer").GetMetadataValue("Version");
	public bool OpenInRunPanel { get; set; }
	public StandardIo? ProcessStandardIo { get; set; }

	public EventWrapper<Task> ProjectRunFailed { get; } = new(() => Task.CompletedTask);
	public EventWrapper<Task> ProjectStartedRunning { get; } = new(() => Task.CompletedTask);
	public EventWrapper<Task> ProjectStoppedRunning { get; } = new(() => Task.CompletedTask);

	public ObservableHashSet<SharpIdeDiagnostic> Diagnostics { get; internal set; } = [];
}

public class StandardIo(PipeReader outputReader, PipeWriter stdinWriter)
{
	public PipeReader OutputReader { get; } = outputReader;
	public PipeWriter StdinWriter { get; } = stdinWriter;

	public TaskCompletionSource OutputReadComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource StdinWriteComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
