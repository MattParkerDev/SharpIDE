using SharpDbg.Infrastructure.Debugger;

namespace SharpIDE.Application.Features.Debugging;

public class StackFrameModel
{
	public required int Id { get; set; }
	public required int ThreadId { get; set; }
	public required string Name { get; set; }
	public required int? Line { get; set; }
	public required int? Column { get; set; }
	public required int? EndLine { get; set; }
	public required int? EndColumn { get; set; }
	public required string? Source { get; set; }
	public required bool IsExternalCode { get; set; }
	public required bool IsResolved { get; set; }
	public required DecompiledSourceInfo? DecompiledSourceInfo { get; set; }
	public required ManagedStackFrameInfo? ManagedInfo { get; set; }
}

public record struct ManagedStackFrameInfo
{
	public required string ClassName { get; set; }
	public required string MethodName { get; set; }
	public required string Namespace { get; set; }
	public required string AssemblyName { get; set; }
}

public class ThreadModel
{
	public required int Id { get; set; }
	public required string Name { get; set; }
}
