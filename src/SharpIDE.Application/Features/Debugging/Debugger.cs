using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Debugging;

// TODO: Why does this exist separate from DebuggingService?
public class Debugger
{
	public required SharpIdeProjectModel Project { get; init; }
	public required int ProcessId { get; init; }
	private DebuggingService _debuggingService = new DebuggingService();
	public async Task Attach(string? debuggerExecutablePath, Dictionary<SharpIdeFile, List<Breakpoint>> breakpointsByFile, CancellationToken cancellationToken)
	{
		await _debuggingService.Attach(ProcessId, debuggerExecutablePath, breakpointsByFile, cancellationToken);
	}

	public async Task StepOver(int threadId, CancellationToken cancellationToken = default) => await _debuggingService.StepOver(threadId, cancellationToken);
	public async Task<List<ThreadModel2>> GetThreadsAtStopPoint() => await _debuggingService.GetThreadsAtStopPoint();
	public async Task<List<StackFrameModel2>> GetStackFramesForThread(int threadId) => await _debuggingService.GetStackFramesForThread(threadId);
	public async Task<List<Variable>> GetVariablesForStackFrame(int frameId) => await _debuggingService.GetVariablesForStackFrame(frameId);
}
