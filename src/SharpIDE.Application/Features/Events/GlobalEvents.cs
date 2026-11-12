using System.Collections.Immutable;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Events;

public class GlobalEvents
{
	public static GlobalEvents Instance { get; set; } = null!;
	public EventWrapper<Task> ProjectsRunningChanged { get; } = new(() => Task.CompletedTask);
	public EventWrapper<Task> StartedRunningProject { get; } = new(() => Task.CompletedTask);
	public EventWrapper<SharpIdeProjectModel, Task> ProjectStartedDebugging { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<SharpIdeProjectModel, Task> ProjectStoppedDebugging { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<SharpIdeProjectModel, Task> ProjectStartedRunning { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<SharpIdeProjectModel, Task> ProjectStoppedRunning { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<ExecutionStopInfo, Task> DebuggerExecutionStopped { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<SharpIdeProjectModel, Task> DebuggerExecutionContinued { get; } = new(_ => Task.CompletedTask);
	/// A document changed, project was reloaded etc. Document changes include unsaved changes in the IDE.
	public EventWrapper<Task> SolutionAltered { get; } = new(() => Task.CompletedTask);

	public EventWrapper<ImmutableArray<string>, Task> AnalyzerDllsChanged { get; } = new(_ => Task.CompletedTask);
}

