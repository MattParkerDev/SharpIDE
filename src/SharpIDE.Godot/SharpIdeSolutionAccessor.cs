using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot;

public class SharpIdeSolutionAccessor
{
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;
	public TaskCompletionSource SolutionReadyTcs { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}
