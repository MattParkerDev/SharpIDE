using NuGet.Packaging.Core;
using NuGet.ProjectModel;

namespace SharpIDE.Application.Features.Evaluation;

public class Dependent
{
	public required string PackageName { get; set; }
	public required PackageDependency PackageDependency { get; set; }
}
public static class NugetDependencyGraph
{
	/// For one target (framework, and optionally RID) of an assets file - a multi-targeted project has a target per framework
	internal static Dictionary<string, List<Dependent>> GetPackageDependencyMap(LockFileTarget target)
	{
		var parentMap = new Dictionary<string, List<Dependent>>(StringComparer.OrdinalIgnoreCase);

		foreach (var library in target.Libraries)
		{
			var dependencies = library.Dependencies;
			foreach (var packageDependency in dependencies)
			{
				var mapEntry = parentMap!.GetValueOrDefault(packageDependency.Id, []);
				var dependent = new Dependent
				{
					PackageName = library.Name!,
					PackageDependency = packageDependency
				};
				mapEntry.Add(dependent);
				parentMap[packageDependency.Id] = mapEntry;
			}
		}

		return parentMap;
	}
}
