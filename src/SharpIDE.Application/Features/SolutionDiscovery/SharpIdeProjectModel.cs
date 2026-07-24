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
	public ReactiveProperty<MsBuildProjectInstanceLoadResult> ActiveMsBuildProjectLoadResult { get; }
	public ReactiveProperty<IReadOnlyList<MsBuildProjectInstanceLoadResult>> TfmSpecificLoadResults { get; }
	public required Task<MsBuildProjectLoadResult> MsBuildEvaluationProjectTask { get; set; }

	[SetsRequiredMembers]
	internal SharpIdeProjectModel(IntermediateProjectModel projectModel, ConcurrentBag<SharpIdeProjectModel> allProjects, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders, IExpandableSharpIdeNode parent, SharpIdeRootFolder sharpIdeRootFolder)
	{
		Parent = parent;
		Name = new ReactiveProperty<string>(projectModel.Model.ActualDisplayName);
		FilePath = projectModel.FullFilePath;
		DirectoryPath = Path.GetDirectoryName(projectModel.FullFilePath)!;
		Folder = sharpIdeRootFolder.GetFolderForProject(projectModel.FullFilePath);
		ActiveMsBuildProjectLoadResult = new ReactiveProperty<MsBuildProjectInstanceLoadResult>(new MsBuildProjectInstanceLoadResult
		{
			LoadState = MsBuildProjectLoadState.Loading
		});
		TfmSpecificLoadResults = new ReactiveProperty<IReadOnlyList<MsBuildProjectInstanceLoadResult>>([]);
		MsBuildEvaluationProjectTask = LoadOrReloadProjectInMsBuild();
		allProjects.Add(this);
	}

	public async Task<MsBuildProjectLoadResult> LoadOrReloadProjectInMsBuild()
	{
		TfmSpecificLoadResults.Value = [];
		ActiveMsBuildProjectLoadResult.Value = new MsBuildProjectInstanceLoadResult
		{
			LoadState = MsBuildProjectLoadState.Loading
		};
		return await Task.Run(async () =>
		{
			if (Folder is null)
			{
				var missingResult = new MsBuildProjectInstanceLoadResult
				{
					LoadState = MsBuildProjectLoadState.Missing
				};
				ActiveMsBuildProjectLoadResult.Value = missingResult;
				return new MsBuildProjectLoadResult
				{
					OuterProjectLoadResult = missingResult,
					DefaultActiveProjectLoadResult = missingResult,
					TfmSpecificLoadResults = []
				};
			}
			var result = await ProjectEvaluation.LoadOrReloadProject(FilePath);
			Diagnostics.RemoveRange(Diagnostics.set); // Clear regardless
			if (result.DefaultActiveProjectLoadResult.LoadState is MsBuildProjectLoadState.Invalid)
			{
				Guard.Against.Null(result.DefaultActiveProjectLoadResult.Diagnostic);
				Diagnostics.Add(result.DefaultActiveProjectLoadResult.Diagnostic);
			}

			TfmSpecificLoadResults.Value = result.TfmSpecificLoadResults;
			ActiveMsBuildProjectLoadResult.Value = result.DefaultActiveProjectLoadResult;
			return result;
		});
	}

	public Project ActiveMsBuildEvaluationProject => ActiveMsBuildProjectLoadResult.Value.Project
		?? throw new InvalidOperationException("Do not attempt to access the MsBuildEvaluationProject before it has been loaded");

	public bool IsLoading => ActiveMsBuildProjectLoadResult.Value.LoadState is MsBuildProjectLoadState.Loading;
	public bool IsLoaded => ActiveMsBuildProjectLoadResult.Value.LoadState is MsBuildProjectLoadState.Loaded;
	public bool IsInvalid => ActiveMsBuildProjectLoadResult.Value.LoadState is MsBuildProjectLoadState.Invalid or MsBuildProjectLoadState.Missing;
	public bool IsRunnable => ActiveMsBuildEvaluationProject.GetPropertyValue("OutputType") is "Exe" or "WinExe" || IsBlazorProject || IsGodotProject;
	public bool IsBlazorProject => ActiveMsBuildEvaluationProject.Xml.Sdk is "Microsoft.NET.Sdk.BlazorWebAssembly";
	public bool IsGodotProject => ActiveMsBuildEvaluationProject.Xml.Sdk.StartsWith("Godot.NET.Sdk");
	public bool IsMtpTestProject => ActiveMsBuildEvaluationProject.GetPropertyValue("IsTestingPlatformApplication") is "true";
	public string BlazorDevServerVersion => ActiveMsBuildEvaluationProject.Items.Single(s => s.ItemType is "PackageReference" && s.EvaluatedInclude is "Microsoft.AspNetCore.Components.WebAssembly.DevServer").GetMetadataValue("Version");
	public string RootNamespace => ActiveMsBuildEvaluationProject.GetPropertyValue("RootNamespace");
	public string TargetFramework => ActiveMsBuildEvaluationProject.GetPropertyValue("TargetFramework");
	public string RunCommand => ActiveMsBuildEvaluationProject.GetPropertyValue("RunCommand");
	public string RunArguments => ActiveMsBuildEvaluationProject.GetPropertyValue("RunArguments");
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
