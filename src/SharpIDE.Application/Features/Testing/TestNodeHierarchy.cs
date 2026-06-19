using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Application.Features.Testing;

public sealed record TestNodeHierarchy
{
	public required string Uid { get; init; }
	public required string DisplayName { get; init; }
	public required TestNodeHierarchyKind Kind { get; init; }
	public required int IndentLevel { get; init; }
	public required string[] AncestorUids { get; init; }
	public TestNode? TestNode { get; init; }
	public bool IsGroup => Kind is not TestNodeHierarchyKind.Test;
}

public enum TestNodeHierarchyKind
{
	Namespace,
	Type,
	Test
}
