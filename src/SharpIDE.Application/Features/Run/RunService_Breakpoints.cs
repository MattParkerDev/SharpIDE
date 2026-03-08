using System.Collections.Concurrent;
using Ardalis.GuardClauses;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Run;

public partial class RunService
{
	public ConcurrentDictionary<SharpIdeFile, List<Breakpoint>> Breakpoints { get; } = [];
	public async Task AddBreakpointForFile(SharpIdeFile file, int line)
	{
		Guard.Against.Null(file);

		var breakpoints = Breakpoints.GetOrAdd(file, []);
		var breakpoint = new Breakpoint { Line = line };
		breakpoints.Add(breakpoint);
		if (_debuggerSessionId is not null)
		{
			await _debuggingService.SetBreakpointsForFile(_debuggerSessionId!.Value, file, breakpoints);
		}
	}

	public async Task RemoveBreakpointForFile(SharpIdeFile file, int line)
	{
		Guard.Against.Null(file);
		var breakpoints = Breakpoints.GetOrAdd(file, []);
		var breakpoint = breakpoints.Single(b => b.Line == line);
		breakpoints.Remove(breakpoint);
		if (_debuggerSessionId is not null)
		{
			await _debuggingService.SetBreakpointsForFile(_debuggerSessionId!.Value, file, breakpoints);
		}
	}
}
