using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Debugging;

public class ExecutionStopInfo
{
    public required string FilePath { get; set; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndColumn { get; init; }
    public required int ThreadId { get; init; }
    // Currently assuming only one instance of a project can be debugged at a time
    public required SharpIdeProjectModel Project { get; init; }
    public required DecompiledSourceInfo? DecompiledSourceInfo { get; init; }
}
