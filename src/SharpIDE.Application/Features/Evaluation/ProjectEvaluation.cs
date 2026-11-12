using Ardalis.GuardClauses;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using NuGet.Versioning;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Evaluation;

public enum MsBuildProjectLoadState
{
	Loading = 1,
	Loaded,
	Unloaded,
	Invalid,
	Missing
}

public sealed record MsBuildProjectLoadResult
{
	/// Always set, in a single TFM project, this is both the "outer" and main project. In multi-TFM project scenarios, this is the outer project that has TargetFrameworks set
	public required MsBuildProjectInstanceLoadResult OuterProjectLoadResult { get; set; }
	public required List<MsBuildProjectInstanceLoadResult> TfmSpecificLoadResults { get; set; }
	public required MsBuildProjectInstanceLoadResult DefaultActiveProjectLoadResult { get; set; }
}

public sealed record MsBuildProjectInstanceLoadResult
{
	public MsBuildProjectLoadState LoadState { get; init; }
	/// Set when LoadState is Loaded
	public MsBuildProjectSnapshot? Snapshot { get; init; }
	public SharpIdeDiagnostic? Diagnostic { get; init; }
}

/// What the IDE needs from an evaluation of one project instance (the outer project, or one TFM), evaluated in SharpIDE.MsBuildHost.
/// Equality is by reference - each evaluation is a new snapshot.
public sealed class MsBuildProjectSnapshot(IReadOnlyDictionary<string, string> properties, string sdk, IReadOnlyList<MsBuildPackageReference> packageReferences)
{
	public IReadOnlyDictionary<string, string> Properties { get; } = properties;
	/// The Sdk attribute of the Project element, e.g. Microsoft.NET.Sdk.BlazorWebAssembly. Empty if not set there.
	public string Sdk { get; } = sdk;
	public IReadOnlyList<MsBuildPackageReference> PackageReferences { get; } = packageReferences;

	/// Empty if the property isn't set, like Project.GetPropertyValue
	public string GetPropertyValue(string name) => Properties.GetValueOrDefault(name) ?? string.Empty;
}

public sealed record MsBuildPackageReference(string Name, string? Version);

public static class ProjectEvaluation
{
	internal static MsBuildProjectInstanceLoadResult GetActiveProjectLoadResult(List<MsBuildProjectInstanceLoadResult> loadResults)
	{
		var loadedProjects = loadResults
			.Where(result => result is { LoadState: MsBuildProjectLoadState.Loaded, Snapshot: not null })
			.Select(result => (Result: result, Framework: NuGetFramework.Parse(result.Snapshot!.GetPropertyValue("TargetFramework"))))
			.ToList();

		if (loadedProjects.Count is 0) return loadResults[0];

		var candidateProjects = loadedProjects.Where(project => project.Framework.IsDesktop() is false).ToList();
		if (candidateProjects.Count is 0) candidateProjects = loadedProjects;

		if (candidateProjects.Any(project => project.Framework.Framework is FrameworkConstants.FrameworkIdentifiers.NetCoreApp))
		{
			candidateProjects = candidateProjects
				.Where(project => project.Framework.Framework is FrameworkConstants.FrameworkIdentifiers.NetCoreApp)
				.ToList();
		}

		return candidateProjects
			.OrderByDescending(project => project.Framework, NuGetFrameworkSorter.Instance)
			.Select(project => project.Result)
			.First();
	}

	public static async Task<List<InstalledPackage>> GetPackageReferencesForProjects(List<SharpIdeProjectModel> projectModels, bool includeTransitive = true)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(GetPackageReferencesForProjects)}");
		Guard.Against.Null(projectModels, nameof(projectModels));

		var projects = projectModels.Select(s =>
		{
			var proj = s.ActiveMsBuildSnapshot;
			var assetsPath = proj.GetPropertyValue("ProjectAssetsFile");

			if (File.Exists(assetsPath) is false)
			{
				throw new FileNotFoundException("Could not find project.assets.json file", assetsPath);
			}
			var lockFileFormat = new LockFileFormat();
			var lockFile = lockFileFormat.Read(assetsPath);
			return (LockFile: lockFile, Project: s);
		}).ToList();

		var result = await GetPackagesFromAssetsFiles(projects);
		return result;
	}

	public static async Task<List<InstalledPackage>> GetPackagesFromAssetsFiles(List<(LockFile, SharpIdeProjectModel)> projects, bool includeTransitive = true)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(GetPackagesFromAssetsFiles)}");
		var allPackages = new Dictionary<string, InstalledPackage>(StringComparer.OrdinalIgnoreCase);
		foreach (var (assetsFile, project) in projects)
		{
			var dependencyMap = NugetDependencyGraph.GetPackageDependencyMap(assetsFile);

			var activeTargetFramework = project.ActiveMsBuildSnapshot.GetPropertyValue("TargetFramework");
			var target = assetsFile.Targets.FirstOrDefault(t =>
				t.RuntimeIdentifier == null &&
				t.TargetFramework.GetShortFolderName().Equals(activeTargetFramework, StringComparison.OrdinalIgnoreCase));
			if (target == null) continue;

			var tfm = target.TargetFramework.GetShortFolderName();
			var tfmInfo = assetsFile.PackageSpec.TargetFrameworks
				.FirstOrDefault(t => t.FrameworkName.Equals(target.TargetFramework));

			if (tfmInfo == null) continue;

			var topLevelDependencies = tfmInfo.Dependencies
				.DistinctBy(s => s.Name)
				.Select(s => s.Name)
				.ToHashSet();

			foreach (var lockFileTargetLibrary in target.Libraries.Where(l => l.Type == "package"))
			{
				if (string.IsNullOrEmpty(lockFileTargetLibrary.Name) || lockFileTargetLibrary.Version is null) continue;

				var isTopLevel = topLevelDependencies.Contains(lockFileTargetLibrary.Name);
				if (!includeTransitive && !isTopLevel) continue;

				var dependency = tfmInfo.Dependencies
					.FirstOrDefault(d => d.Name.Equals(lockFileTargetLibrary.Name, StringComparison.OrdinalIgnoreCase));

				var dependents = dependencyMap.GetValueOrDefault(lockFileTargetLibrary.Name, []);
				var mappedDependents = dependents.Select(d => new DependentPackage
				{
					PackageName = d.PackageName,
					RequestedVersion = d.PackageDependency.VersionRange
				}).ToList();

				var existingPackage = allPackages.GetValueOrDefault(lockFileTargetLibrary.Name) ?? new InstalledPackage { Name = lockFileTargetLibrary.Name, ProjectPackageReferences = [] };
				existingPackage.ProjectPackageReferences.Add(new ProjectPackageReference
				{
					Project = project,
					InstalledVersion = lockFileTargetLibrary.Version,
					IsTopLevel = isTopLevel,
					IsAutoReferenced = dependency?.AutoReferenced ?? false,
					DependentPackages = mappedDependents
				});
				allPackages[lockFileTargetLibrary.Name] = existingPackage;
			}
		}
		return allPackages.Values.ToList();
	}
}

public class InstalledPackage
{
	public required string Name { get; set; }
	//public required NuGetVersion LatestVersion { get; set; }

	/// <summary>
	/// Projects that reference this package
	/// </summary>
	public required List<ProjectPackageReference> ProjectPackageReferences { get; set; }
}

public class ProjectPackageReference
{
	public required SharpIdeProjectModel Project { get; set; }
	public required NuGetVersion InstalledVersion { get; set; }
	public required bool IsTopLevel { get; set; }
	public required bool IsAutoReferenced { get; set; }
	public List<DependentPackage>? DependentPackages { get; set; }
	public bool IsTransitive => !IsTopLevel && !IsAutoReferenced;
}

public class DependentPackage
{
	public required string PackageName { get; set; }
	public required VersionRange RequestedVersion { get; set; }
}
