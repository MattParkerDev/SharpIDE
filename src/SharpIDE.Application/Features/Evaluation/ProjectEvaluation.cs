using Ardalis.GuardClauses;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using NuGet.Versioning;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using Project = Microsoft.Build.Evaluation.Project;

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
	public required MsBuildProjectInstanceLoadResult ActiveProjectLoadResult { get; set; }
}

public sealed record MsBuildProjectInstanceLoadResult
{
	public MsBuildProjectLoadState LoadState { get; set; }
	public Project? Project { get; init; }
	public SharpIdeDiagnostic? Diagnostic { get; init; }
}

public static class ProjectEvaluation
{
	private static readonly ProjectCollection _projectCollection = ProjectCollection.GlobalProjectCollection;
	public static async Task<MsBuildProjectLoadResult> LoadOrReloadProject(string projectFilePath)
	{
		using var __ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(LoadOrReloadProject)}");
		Guard.Against.Null(projectFilePath, nameof(projectFilePath));

		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		if (File.Exists(projectFilePath) is false)
		{
			var missingResult = new MsBuildProjectInstanceLoadResult
			{
				LoadState = MsBuildProjectLoadState.Missing
			};
			return new MsBuildProjectLoadResult
			{
				OuterProjectLoadResult = missingResult,
				TfmSpecificLoadResults = [],
				ActiveProjectLoadResult = missingResult
			};
		}

		try
		{
			var loadedProjects = _projectCollection.GetLoadedProjects(projectFilePath);
			var outerProject = loadedProjects.FirstOrDefault(project => project.GlobalProperties.ContainsKey("TargetFramework") is false);
			if (outerProject is not null)
			{
				var projectRootElement = outerProject.Xml;
				projectRootElement.Reload(false);
				outerProject.ReevaluateIfNecessary();
			}
			else
			{
				outerProject = _projectCollection.LoadProject(projectFilePath);
			}

			var outerProjectLoadResult = new MsBuildProjectInstanceLoadResult
			{
				LoadState = MsBuildProjectLoadState.Loaded,
				Project = outerProject
			};

			var targetFrameworks = outerProject.GetPropertyValue("TargetFrameworks")
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (targetFrameworks.Count == 0)
			{
				foreach (var innerProject in loadedProjects.Where(project => project.GlobalProperties.ContainsKey("TargetFramework")))
				{
					_projectCollection.UnloadProject(innerProject);
				}

				return new MsBuildProjectLoadResult
				{
					OuterProjectLoadResult = outerProjectLoadResult,
					TfmSpecificLoadResults = [],
					ActiveProjectLoadResult = outerProjectLoadResult
				};
			}

			var desiredTargetFrameworks = targetFrameworks.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var innerProjectsByTargetFramework = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
			foreach (var innerProject in loadedProjects.Where(project => project.GlobalProperties.TryGetValue("TargetFramework", out _)))
			{
				var targetFramework = innerProject.GlobalProperties["TargetFramework"];
				if (desiredTargetFrameworks.Contains(targetFramework) is false || innerProjectsByTargetFramework.TryAdd(targetFramework, innerProject) is false)
				{
					_projectCollection.UnloadProject(innerProject);
				}
			}

			var tfmSpecificLoadResults = new List<MsBuildProjectInstanceLoadResult>(targetFrameworks.Count);
			foreach (var targetFramework in targetFrameworks)
			{
				try
				{
					if (innerProjectsByTargetFramework.TryGetValue(targetFramework, out var innerProject))
					{
						innerProject.ReevaluateIfNecessary();
					}
					else
					{
						innerProject = _projectCollection.LoadProject(projectFilePath, new Dictionary<string, string>
						{
							["TargetFramework"] = targetFramework
						}, toolsVersion: null);
					}

					tfmSpecificLoadResults.Add(new MsBuildProjectInstanceLoadResult
					{
						LoadState = MsBuildProjectLoadState.Loaded,
						Project = innerProject
					});
				}
				catch (InvalidProjectFileException ex)
				{
					tfmSpecificLoadResults.Add(new MsBuildProjectInstanceLoadResult
					{
						LoadState = MsBuildProjectLoadState.Invalid,
						Diagnostic = ex.ToDiagnostic()
					});
				}
			}

			return new MsBuildProjectLoadResult
			{
				OuterProjectLoadResult = outerProjectLoadResult,
				TfmSpecificLoadResults = tfmSpecificLoadResults,
				ActiveProjectLoadResult = GetActiveProjectLoadResult(tfmSpecificLoadResults)
			};
		}
		catch (InvalidProjectFileException ex)
		{
			var invalidResult = new MsBuildProjectInstanceLoadResult
			{
				LoadState = MsBuildProjectLoadState.Invalid,
				Diagnostic = ex.ToDiagnostic()
			};
			return new MsBuildProjectLoadResult
			{
				OuterProjectLoadResult = invalidResult,
				TfmSpecificLoadResults = [],
				ActiveProjectLoadResult = invalidResult
			};
		}
	}

	private static MsBuildProjectInstanceLoadResult GetActiveProjectLoadResult(List<MsBuildProjectInstanceLoadResult> loadResults)
	{
		var loadedProjects = loadResults
			.Where(result => result is { LoadState: MsBuildProjectLoadState.Loaded, Project: not null })
			.Select(result => (Result: result, Framework: NuGetFramework.Parse(result.Project!.GetPropertyValue("TargetFramework"))))
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

	public static void ClearLoadedProjects()
	{
		_projectCollection.UnloadAllProjects();
	}

	public static Guid GetOrCreateDotnetUserSecretsId(SharpIdeProjectModel projectModel)
	{
		Guard.Against.Null(projectModel, nameof(projectModel));

		var project = _projectCollection.GetLoadedProjects(projectModel.FilePath).Single(project => project.GlobalProperties.ContainsKey("TargetFramework") is false);
		var projectRootElement = project.Xml;
		var userSecretsId = project.GetPropertyValue("UserSecretsId");
		if (string.IsNullOrWhiteSpace(userSecretsId))
		{
			var newGuid = Guid.NewGuid();
			var property = projectRootElement.AddProperty("UserSecretsId", newGuid.ToString());
			project.Save();
			return newGuid;
		}
		return Guid.Parse(userSecretsId);
	}

	public static async Task<List<InstalledPackage>> GetPackageReferencesForProjects(List<SharpIdeProjectModel> projectModels, bool includeTransitive = true)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(GetPackageReferencesForProjects)}");
		Guard.Against.Null(projectModels, nameof(projectModels));

		var projects = projectModels.Select(s =>
		{
			var proj = s.ActiveMsBuildEvaluationProject;
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

			var activeTargetFramework = project.ActiveMsBuildEvaluationProject.GetPropertyValue("TargetFramework");
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

	private static SharpIdeDiagnostic ToDiagnostic(this InvalidProjectFileException ex)
	{
		var linePosition = new LinePosition(ex.LineNumber - 1, ex.ColumnNumber - 1);
		var linePositionSpan = new LinePositionSpan(linePosition, linePosition);
		var diagnostic = Diagnostic.Create(new DiagnosticDescriptor(id: ex.ErrorCode, title: string.Empty, ex.BaseMessage, ex.ErrorSubcategory ?? "MSBuild", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: ex.HelpLink), Location.Create(ex.ProjectFile, TextSpan.FromBounds(0, 0), linePositionSpan));
		return new SharpIdeDiagnostic(linePositionSpan, diagnostic, ex.ProjectFile);
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
