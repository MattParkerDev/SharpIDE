namespace SharpIDE.Application.Features.SolutionDiscovery;

public class Folder
{
	public required string Name { get; set; }
	public required string FullName { get; set; }
	public required Folder? ParentFolder { get; set; }
	public required List<MyFile> Files { get; set; } = [];
}

public class MyFile
{
	public required string Name { get; set; }
}
