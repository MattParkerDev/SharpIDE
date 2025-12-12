namespace SharpIDE.Application.Features.Debugging;

public class StackFrameModel2
{
	public required int Id { get; set; }
	public required string Name { get; set; }
	public required int? Line { get; set; }
	public required int? Column { get; set; }
	public required string? Source { get; set; }
	public required bool IsExternalCode { get; set; }
	public required ManagedStackFrameInfo? ManagedInfo { get; set; }
}

public record struct ManagedStackFrameInfo
{
	public required string ClassName { get; set; }
	public required string MethodName { get; set; }
	public required string Namespace { get; set; }
	public required string AssemblyName { get; set; }
}

public class ThreadModel2
{
	public required int Id { get; set; }
	public required string Name { get; set; }
}
