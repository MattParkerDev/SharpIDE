using System.Diagnostics.CodeAnalysis;
using SharpIDE.Application.Features.FileSystem;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeSolutionItemFile : ISharpIdeNode
{
	public required string Path { get; set; }
	public required string Name { get; set; }
	public required string Extension { get; set; }
	public required IExpandableSharpIdeNode Parent { get; set; }
	public required SharpIdeFile? File { get; set; }

	[SetsRequiredMembers]
	internal SharpIdeSolutionItemFile(string fullPath, string name, string extension, IExpandableSharpIdeNode parent, SharpIdeRootFolder sharpIdeRootFolder)
	{
		Path = fullPath;
		Name = name;
		Extension = extension;
		Parent = parent;
		File = sharpIdeRootFolder.AllFiles.GetValueOrDefault(fullPath);
	}
}
