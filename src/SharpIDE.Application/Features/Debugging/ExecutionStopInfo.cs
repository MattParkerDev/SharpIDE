using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Debugging;

public class ExecutionStopInfo
{
    public required int ThreadId { get; init; }
    // Currently assuming only one instance of a project can be debugged at a time
    public required SharpIdeProjectModel Project { get; init; }
    public required DecompiledSourceInfo? DecompiledSourceInfo { get; init; }
}
