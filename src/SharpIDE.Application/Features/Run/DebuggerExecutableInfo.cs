namespace SharpIDE.Application.Features.Run;

public readonly record struct DebuggerExecutableInfo
{
	public required bool UseInMemorySharpDbg { get; init; }
	public required string? DebuggerExecutablePath { get; init; }
}
