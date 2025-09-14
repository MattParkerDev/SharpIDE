using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeFile : ISharpIdeNode, IChildSharpIdeNode
{
	public required IExpandableSharpIdeNode Parent { get; set; }
	public required string Path { get; set; }
	public required string Name { get; set; }
	public bool IsRazorFile => Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

	[SetsRequiredMembers]
	internal SharpIdeFile(string fullPath, string name, IExpandableSharpIdeNode parent, ConcurrentBag<SharpIdeFile> allFiles)
	{
		Path = fullPath;
		Name = name;
		Parent = parent;
		allFiles.Add(this);
	}
}
