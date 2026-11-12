using System.Collections.Concurrent;
using Ardalis.GuardClauses;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.LibraryModel;
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

	/// Only the sections the NuGet panel reads - the libraries, logs and package folders sections are skipped, and libraries is usually the largest
	private const LockFileReadFlags AssetsFileReadFlags = LockFileReadFlags.Targets | LockFileReadFlags.PackageSpec;
	/// Keyed by assets file path. A restore rewrites the file, so an entry is current while the file's last write time and length are unchanged
	private static readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, long Length, LockFile LockFile)> AssetsFileCache = new(StringComparer.OrdinalIgnoreCase);

	/// Projects that are not loaded, or have no readable project.assets.json (not restored yet), are skipped and returned in UnrestoredProjects
	public static async Task<InstalledPackagesResult> GetPackageReferencesForProjects(List<SharpIdeProjectModel> projectModels, bool includeTransitive = true, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(GetPackageReferencesForProjects)}");
		Guard.Against.Null(projectModels, nameof(projectModels));

		var reads = await Task.WhenAll(projectModels.Select(project => Task.Run(() => (Project: project, LockFile: ReadAssetsFile(project)), cancellationToken))).ConfigureAwait(false);

		var restoredProjects = reads.Where(r => r.LockFile is not null).Select(r => (r.LockFile!, r.Project)).ToList();
		var unrestoredProjects = reads.Where(r => r.LockFile is null).Select(r => r.Project).ToList();
		return new InstalledPackagesResult(GetPackagesFromAssetsFiles(restoredProjects, includeTransitive), unrestoredProjects);
	}

	/// Null when the project isn't loaded, or its assets file is missing, unreadable (e.g. mid-restore) or not a valid assets file
	private static LockFile? ReadAssetsFile(SharpIdeProjectModel project)
	{
		var snapshot = project.ActiveMsBuildProjectLoadResult.Value.Snapshot;
		if (snapshot is null) return null;
		var assetsPath = snapshot.GetPropertyValue("ProjectAssetsFile");
		if (string.IsNullOrEmpty(assetsPath)) return null;

		var fileInfo = new FileInfo(assetsPath);
		if (fileInfo.Exists is false)
		{
			AssetsFileCache.TryRemove(assetsPath, out _);
			return null;
		}
		if (AssetsFileCache.TryGetValue(assetsPath, out var cached) && cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc && cached.Length == fileInfo.Length)
		{
			return cached.LockFile;
		}

		LockFile? lockFile;
		try
		{
			lockFile = LockFileUtilities.GetLockFile(assetsPath, NullLogger.Instance, AssetsFileReadFlags);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
		if (lockFile is null || lockFile.PackageSpec is null || lockFile.Targets is null or { Count: 0 }) return null;

		AssetsFileCache[assetsPath] = (fileInfo.LastWriteTimeUtc, fileInfo.Length, lockFile);
		return lockFile;
	}

	public static List<InstalledPackage> GetPackagesFromAssetsFiles(List<(LockFile, SharpIdeProjectModel)> projects, bool includeTransitive = true)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluation)}.{nameof(GetPackagesFromAssetsFiles)}");
		var allPackages = new Dictionary<string, InstalledPackage>(StringComparer.OrdinalIgnoreCase);
		foreach (var (assetsFile, project) in projects)
		{
			// The project's TargetFramework is the alias it gives in TargetFrameworks, which the target carries - it needn't be the framework's short folder name (net8.0-windows is net8.0-windows7.0)
			var activeTargetFramework = project.ActiveMsBuildSnapshot.GetPropertyValue("TargetFramework");
			var target = assetsFile.Targets.FirstOrDefault(t => t.RuntimeIdentifier == null && string.Equals(t.TargetAlias, activeTargetFramework, StringComparison.OrdinalIgnoreCase))
				?? assetsFile.Targets.FirstOrDefault(t => t.RuntimeIdentifier == null && t.TargetFramework.GetShortFolderName().Equals(activeTargetFramework, StringComparison.OrdinalIgnoreCase));
			if (target == null) continue;

			var tfmInfo = assetsFile.PackageSpec.TargetFrameworks.FirstOrDefault(t => string.IsNullOrEmpty(target.TargetAlias) is false && string.Equals(t.TargetAlias, target.TargetAlias, StringComparison.OrdinalIgnoreCase))
				?? assetsFile.PackageSpec.TargetFrameworks.FirstOrDefault(t => t.FrameworkName.Equals(target.TargetFramework));

			if (tfmInfo == null) continue;

			var dependencyMap = NugetDependencyGraph.GetPackageDependencyMap(target);

			var topLevelDependencies = new Dictionary<string, LibraryDependency>(StringComparer.OrdinalIgnoreCase);
			foreach (var libraryDependency in tfmInfo.Dependencies) topLevelDependencies.TryAdd(libraryDependency.Name, libraryDependency);

			foreach (var lockFileTargetLibrary in target.Libraries.Where(l => l.Type == "package"))
			{
				if (string.IsNullOrEmpty(lockFileTargetLibrary.Name) || lockFileTargetLibrary.Version is null) continue;

				var isTopLevel = topLevelDependencies.TryGetValue(lockFileTargetLibrary.Name, out var dependency);
				if (!includeTransitive && !isTopLevel) continue;

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

public sealed record InstalledPackagesResult(List<InstalledPackage> Packages, List<SharpIdeProjectModel> UnrestoredProjects);

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
