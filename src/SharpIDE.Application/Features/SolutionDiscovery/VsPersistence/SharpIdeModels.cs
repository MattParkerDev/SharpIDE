using System.Threading.Channels;
using Microsoft.Build.Evaluation;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public interface ISharpIdeNode;

public class SharpIdeSolutionModel : ISharpIdeNode
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required List<SharpIdeProjectModel> Projects { get; set; }
	public required List<SharpIdeSolutionFolder> Folders { get; set; }
	public required HashSet<SharpIdeProjectModel> AllProjects { get; set; }
}
public class SharpIdeSolutionFolder : ISharpIdeNode
{
	public required string Name { get; set; }
	public required List<SharpIdeSolutionFolder> Folders { get; set; }
	public required List<SharpIdeProjectModel> Projects { get; set; }
	public required List<SharpIdeFile> Files { get; set; }
	public bool Expanded { get; set; }
}
public class SharpIdeProjectModel : ISharpIdeNode
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required List<SharpIdeFolder> Folders { get; set; }
	public required List<SharpIdeFile> Files { get; set; }
	public bool Expanded { get; set; }
	public bool Running { get; set; }
	public CancellationTokenSource? RunningCancellationTokenSource { get; set; }
	public required Task<Project> MsBuildEvaluationProjectTask { get; set; }

	public Project MsBuildEvaluationProject => MsBuildEvaluationProjectTask.IsCompletedSuccessfully
		? MsBuildEvaluationProjectTask.Result
		: throw new InvalidOperationException("Do not attempt to access the MsBuildEvaluationProject before it has been loaded");

	public bool IsRunnable => MsBuildEvaluationProject.Xml.Sdk is "Microsoft.NET.Sdk.BlazorWebAssembly" || MsBuildEvaluationProject.GetPropertyValue("OutputType") is "Exe" or "WinExe";
	public bool OpenInRunPanel { get; set; }
	public Channel<byte[]>? RunningOutputChannel { get; set; }
	public event Func<Task> ProjectStartedRunning = () => Task.CompletedTask;
	public void InvokeProjectStartedRunning() => ProjectStartedRunning?.Invoke();
}
