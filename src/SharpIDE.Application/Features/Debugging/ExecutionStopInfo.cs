using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Debugging;

public class ExecutionStopInfo
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int ThreadId { get; init; }
    // Currently assuming only one instance of a project can be debugged at a time
    public required SharpIdeProjectModel Project { get; init; }
}
