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

	public FileSystemWatcherInternal FileSystemWatcherInternal { get; } = new();
	public EventWrapper<ImmutableArray<string>, Task> AnalyzerDllsChanged { get; } = new(_ => Task.CompletedTask);
}

/// <summary>
/// All file system events for files we have filtered for, e.g. excluding bin/obj folders. Does not filter out events triggered by us
/// </summary>
public class FileSystemWatcherInternal
{
	public EventWrapper<string, Task> DirectoryCreated { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<string, Task> DirectoryDeleted { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<string, string, Task> DirectoryRenamed { get; } = new((_, _) => Task.CompletedTask);
	public EventWrapper<string, Task> FileCreated { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<string, Task> FileDeleted { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<string, string, Task> FileRenamed { get; } = new((_, _) => Task.CompletedTask);
	public EventWrapper<string, Task> FileChanged { get; } = new(_ => Task.CompletedTask);
}
