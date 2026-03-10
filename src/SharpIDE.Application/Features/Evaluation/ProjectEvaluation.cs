using System.Diagnostics.CodeAnalysis;

using Ardalis.GuardClauses;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ExternalAccess.Razor.Cohost.Handlers;
using Microsoft.CodeAnalysis.Text;

using NuGet.ProjectModel;
using NuGet.Versioning;

using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

using Project = Microsoft.Build.Evaluation.Project;

namespace SharpIDE.Application.Features.Evaluation;

public sealed record ProjectLoadResult
{
	[MemberNotNullWhen(true, nameof(Project))]
	public bool IsLoaded { get; init; }
	[MemberNotNullWhen(true, nameof(Diagnostic))]
	public bool IsInvalid { get; init; }
	public Project? Project { get; init; }
	public SharpIdeDiagnostic? Diagnostic { get; init; }
}

public static class ProjectEvaluation
{
	private static readonly ProjectCollection _projectCollection = ProjectCollection.GlobalProjectCollection;
	public static async Task<ProjectLoadResult> LoadProject(string projectFilePath)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(LoadProject)}");
		Guard.Against.Null(projectFilePath, nameof(projectFilePath));

		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		try
		{
			var project = _projectCollection.LoadProject(projectFilePath);

			//Console.WriteLine($"ProjectEvaluation: loaded {project.FullPath}");
			return new ProjectLoadResult
			{
				IsLoaded = true,
				IsInvalid = false,
				Project = project
			};
		}
		catch (InvalidProjectFileException ex)
		{
			return new ProjectLoadResult
			{
				IsLoaded = false,
				IsInvalid = true,
				Diagnostic = ex.ToDiagnostic()
			};
		}
	}

	public static async Task<ProjectLoadResult> ReloadProject(string projectFilePath)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(ReloadProject)}");
		Guard.Against.Null(projectFilePath, nameof(projectFilePath));

		try
		{
			var project = _projectCollection.GetLoadedProjects(projectFilePath).Single();
			var projectRootElement = project.Xml;
			projectRootElement.Reload(false);
			project.ReevaluateIfNecessary();

			return new ProjectLoadResult
			{
				IsLoaded = true,
				Project = project
			};
		}
		catch (InvalidProjectFileException ex)
		{
			return new ProjectLoadResult
			{
				IsLoaded = false,
				IsInvalid = true,
				Diagnostic = ex.ToDiagnostic()
			};
		}
	}

	public static Guid GetOrCreateDotnetUserSecretsId(SharpIdeProjectModel projectModel)
	{
		Guard.Against.Null(projectModel, nameof(projectModel));

		var project = _projectCollection.GetLoadedProjects(projectModel.FilePath).Single();
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
			var proj = _projectCollection.GetLoadedProjects(s.FilePath).Single();
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

			// We currently do not handle multi-targeted projects
			var target = assetsFile.Targets.SingleOrDefault(t => t.RuntimeIdentifier == null);
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
				if (string.IsNullOrEmpty(lockFileTargetLibrary.Name)) continue;

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
		var linePosition = new LinePositionSpan(new LinePosition(ex.LineNumber, ex.ColumnNumber), new LinePosition(ex.LineNumber, ex.ColumnNumber));
		var diagnostic = Diagnostic.Create(new DiagnosticDescriptor(id: ex.ErrorCode, title: string.Empty, ex.BaseMessage, ex.ErrorSubcategory ?? "MSBuild", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: ex.HelpLink), Location.Create(ex.ProjectFile, TextSpan.FromBounds(0, 0), linePosition));
		return new SharpIdeDiagnostic(linePosition, diagnostic, ex.ProjectFile);

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
