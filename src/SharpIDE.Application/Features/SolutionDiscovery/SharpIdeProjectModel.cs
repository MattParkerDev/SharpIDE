using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Threading.Channels;
using Ardalis.GuardClauses;
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
	private readonly Lock _evaluationLock = new();
	private TaskCompletionSource<MsBuildProjectLoadResult> _evaluationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	/// Completes when the current (or, before the first evaluation has started, the first) evaluation does. Never faults - a failed evaluation is an Invalid result.
	public Task<MsBuildProjectLoadResult> MsBuildEvaluationProjectTask { get { lock (_evaluationLock) return _evaluationTcs.Task; } }

	[SetsRequiredMembers]
	internal SharpIdeProjectModel(IntermediateProjectModel projectModel, ConcurrentBag<SharpIdeProjectModel> allProjects, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders, IExpandableSharpIdeNode parent, SharpIdeFileTree fileTree)
	{
		Parent = parent;
		Name = new ReactiveProperty<string>(projectModel.Model.ActualDisplayName);
		FilePath = projectModel.FullFilePath;
		DirectoryPath = Path.GetDirectoryName(projectModel.FullFilePath)!;
		Folder = fileTree.GetFolderForProject(projectModel.FullFilePath);
		ActiveMsBuildProjectLoadResult = new ReactiveProperty<MsBuildProjectInstanceLoadResult>(new MsBuildProjectInstanceLoadResult
		{
			LoadState = MsBuildProjectLoadState.Loading
		});
		TfmSpecificLoadResults = new ReactiveProperty<IReadOnlyList<MsBuildProjectInstanceLoadResult>>([]);
		// Evaluated by ProjectEvaluationService, once the solution's SharpIDE.MsBuildHost is up
		allProjects.Add(this);
	}

	/// Called by ProjectEvaluationService as it starts evaluating
	internal void BeginEvaluation()
	{
		lock (_evaluationLock)
		{
			if (_evaluationTcs.Task.IsCompleted) _evaluationTcs = new TaskCompletionSource<MsBuildProjectLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		TfmSpecificLoadResults.Value = [];
		ActiveMsBuildProjectLoadResult.Value = new MsBuildProjectInstanceLoadResult
		{
			LoadState = MsBuildProjectLoadState.Loading
		};
	}

	/// Called by ProjectEvaluationService with each evaluation's result - including re-evaluations it didn't announce with BeginEvaluation (e.g. an imported file changed)
	internal void ApplyEvaluationResult(MsBuildProjectLoadResult result)
	{
		Diagnostics.RemoveRange(Diagnostics.set); // Clear regardless
		if (result.DefaultActiveProjectLoadResult is { LoadState: MsBuildProjectLoadState.Invalid, Diagnostic: { } diagnostic })
		{
			Diagnostics.Add(diagnostic);
		}

		TfmSpecificLoadResults.Value = result.TfmSpecificLoadResults;
		ActiveMsBuildProjectLoadResult.Value = result.DefaultActiveProjectLoadResult;
		lock (_evaluationLock)
		{
			if (_evaluationTcs.TrySetResult(result) is false)
			{
				var completed = new TaskCompletionSource<MsBuildProjectLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
				completed.SetResult(result);
				_evaluationTcs = completed;
			}
		}
	}

	public MsBuildProjectSnapshot ActiveMsBuildSnapshot => ActiveMsBuildProjectLoadResult.Value.Snapshot
		?? throw new InvalidOperationException("Do not attempt to access the MSBuild evaluation snapshot before the project has been loaded");

	public bool IsLoading => ActiveMsBuildProjectLoadResult.Value.LoadState is MsBuildProjectLoadState.Loading;
	public bool IsLoaded => ActiveMsBuildProjectLoadResult.Value.LoadState is MsBuildProjectLoadState.Loaded;
	public bool IsInvalid => ActiveMsBuildProjectLoadResult.Value.LoadState is MsBuildProjectLoadState.Invalid or MsBuildProjectLoadState.Missing;
	// Blazor WASM and Godot projects may not be Exe, but are launched via RunCommand
	public bool IsRunnable => ActiveMsBuildSnapshot.GetPropertyValue("OutputType") is "Exe" or "WinExe" || IsBlazorProject || IsGodotProject || string.IsNullOrWhiteSpace(RunCommand) is false;
	public bool IsBlazorProject => ActiveMsBuildSnapshot.Sdk is "Microsoft.NET.Sdk.BlazorWebAssembly";
	public bool IsGodotProject => ActiveMsBuildSnapshot.Sdk.StartsWith("Godot.NET.Sdk");
	public bool IsMtpTestProject => ActiveMsBuildProjectLoadResult.Value.Snapshot?.GetPropertyValue("IsTestingPlatformApplication") is "true";
	public string RootNamespace => ActiveMsBuildSnapshot.GetPropertyValue("RootNamespace");
	public string TargetFramework => ActiveMsBuildSnapshot.GetPropertyValue("TargetFramework");
	/// As evaluated - the values used to launch come from a Build;ComputeRunArguments build at run time
	public string RunCommand => ActiveMsBuildSnapshot.GetPropertyValue("RunCommand");
	public string RunArguments => ActiveMsBuildSnapshot.GetPropertyValue("RunArguments");
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
