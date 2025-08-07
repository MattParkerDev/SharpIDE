using Microsoft.Build.Evaluation;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public interface ISharpIdeNode;

public class SharpIdeSolutionModel : ISharpIdeNode
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required List<SharpIdeProjectModel> Projects { get; set; }
	public required List<SharpIdeSolutionFolder> Folders { get; set; }
	public required List<SharpIdeProjectModel> AllProjects { get; set; }
}
public class SharpIdeSolutionFolder : ISharpIdeNode
{
	public required string Name { get; set; }
	public required List<SharpIdeSolutionFolder> Folders { get; set; }
	public required List<SharpIdeProjectModel> Projects { get; set; }
	public required List<SharpIdeFile> Files { get; set; }
	public bool Expanded { get; set; }
}
public class SharpIdeProjectModel : ISharpIdeNode
{
	public required string Name { get; set; }
	public required string FilePath { get; set; }
	public required List<SharpIdeFolder> Folders { get; set; }
	public required List<SharpIdeFile> Files { get; set; }
	public bool Expanded { get; set; }
	public required Task<Project> MsBuildEvaluationProject { get; set; }
}
