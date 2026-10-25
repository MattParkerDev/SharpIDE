using R3;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public interface ISharpIdeNode;

public interface IExpandableSharpIdeNode
{
	public bool Expanded { get; set; }
}
public interface ISolutionOrProject
{
	public string DirectoryPath { get; set; }
}
public interface ISolutionOrSolutionFolder : IExpandableSharpIdeNode;
public interface IFileOrFolder : IChildSharpIdeNode
{
	public string Path { get; set; }
	public ReactiveProperty<string> Name { get; set; }
}
public interface IChildSharpIdeNode
{
	public IExpandableSharpIdeNode Parent { get; set; }
}
