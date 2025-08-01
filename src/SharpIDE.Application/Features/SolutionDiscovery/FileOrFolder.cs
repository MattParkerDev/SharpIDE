using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeFile : ISharpIdeNode
{
	public required string Path { get; set; }
	public required string Name { get; set; }
}

public class SharpIdeFolder : ISharpIdeNode
{
	public required string Path { get; set; }
	public required string Name { get; set; }
	public required List<SharpIdeFile> Files { get; set; }
	public required List<SharpIdeFolder> Folders { get; set; }
	// public required int Depth { get; set; }

	public bool Expanded { get; set; }
}
