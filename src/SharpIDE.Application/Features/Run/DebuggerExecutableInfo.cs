namespace SharpIDE.Application.Features.Run;

public readonly record struct DebuggerExecutableInfo
{
	public required bool UseSharpDbg { get; init; }
	public required string? DebuggerExecutablePath { get; init; }
}
