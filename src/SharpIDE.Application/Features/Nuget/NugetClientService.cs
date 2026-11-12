using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using SharpIDE.Application.Features.Evaluation;

namespace SharpIDE.Application.Features.Nuget;

public record InstalledNugetPackageInfo(bool IsPrimarilyTransitive, List<ProjectPackageReference> ProjectPackageReferences);
public record IdePackageResult(string PackageId, List<IdePackageFromSourceResult> PackageFromSources, InstalledNugetPackageInfo? InstalledNugetPackageInfo);
public record struct IdePackageFromSourceResult(IPackageSearchMetadata PackageSearchMetadata, PackageSource Source);
public class NugetClientService
{
	private readonly bool _includePrerelease = false;
	private readonly SourceCacheContext _sourceCacheContext = new SourceCacheContext();
	private readonly ILogger _nugetLogger = NullLogger.Instance;
	public async Task<List<IdePackageResult>> GetTop100Results(string directoryPath, CancellationToken cancellationToken = default)
	{
		var settings = Settings.LoadDefaultSettings(root: directoryPath);
		var packageSourceProvider = new PackageSourceProvider(settings);
		var packageSources = packageSourceProvider.LoadPackageSources().Where(p => p.IsEnabled).ToList();

		var packagesResult = new List<IdePackageResult>();

		foreach (var source in packageSources)
		{
			var repository = Repository.Factory.GetCoreV3(source.Source);
			var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);

			if (searchResource == null)
				continue;

			// Search for top packages (no search term = all)
			var results = await searchResource.SearchAsync(
				searchTerm: string.Empty,
				filters: new SearchFilter(includePrerelease: _includePrerelease),
				skip: 0,
				take: 100,
				log: _nugetLogger,
				cancellationToken: cancellationToken).ConfigureAwait(false);

			var idePackageResults = results.AsValueEnumerable().Select(s => new IdePackageResult(s.Identity.Id, [new IdePackageFromSourceResult(s, source)], null));
			foreach (var idePackageResult in idePackageResults) packagesResult.Add(idePackageResult);
		}

		// Combine, group, and order by download count
		var topPackages = packagesResult
			.GroupBy(p => p.PackageFromSources.First().PackageSearchMetadata.Identity.Id, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.OrderByDescending(p => p.PackageFromSources.First().PackageSearchMetadata.DownloadCount ?? 0)
			.Take(100)
			.ToList();

		// foreach (var package in topPackages)
		// {
		// 	var repository = Repository.Factory.GetCoreV3(package.PackageSources.Single());
		// 	var packageMetadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
		// 	var metadata = await packageMetadataResource.GetMetadataAsync(
		// 		package.PackageSearchMetadata.Identity.Id, includePrerelease: _includePrerelease, includeUnlisted: false,
		// 		cache, logger, cancellationToken).ConfigureAwait(false);
		// 	;
		// 	var packageByIdResource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
		// }

		// we need to find out if other package sources have the package too
		foreach (var package in topPackages)
		{
			foreach (var source in packageSources.Except(package.PackageFromSources.Select(s => s.Source)).ToList())
			{
				var repository = Repository.Factory.GetCoreV3(source.Source);
				var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);

				if (searchResource == null)
					continue;

				// TODO: Might be faster to use FindPackageByIdResource
				var results = await searchResource.SearchAsync(
					searchTerm: package.PackageId,
					filters: new SearchFilter(includePrerelease: _includePrerelease),
					skip: 0,
					take: 2,
					log: _nugetLogger,
					cancellationToken: cancellationToken).ConfigureAwait(false);
				var foundPackage = results.SingleOrDefault(r => r.Identity.Id.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
				if (foundPackage != null)
				{
					package.PackageFromSources.Add(new IdePackageFromSourceResult(foundPackage, source));
				}
			}
		}

		return topPackages;
	}

	public async Task<List<IPackageSearchMetadata>> GetAllVersionsOfPackageInSource(string packageId, PackageSource source, CancellationToken cancellationToken = default)
	{
			var repository = Repository.Factory.GetCoreV3(source);
			var packageMetadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);

			var metadata = await packageMetadataResource.GetMetadataAsync(
				packageId, includePrerelease: _includePrerelease, includeUnlisted: false,
				_sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false);

			//var packageByIdResource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
			var metadataList = metadata.ToList();
			metadataList.Reverse();
			return metadataList;
	}

	public async Task<List<IdePackageResult>> GetPackagesForInstalledPackages(string directoryPath, List<InstalledPackage> installedPackages, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(NugetClientService)}.{nameof(GetPackagesForInstalledPackages)}");
		var settings = Settings.LoadDefaultSettings(root: directoryPath);
		var packageSourceProvider = new PackageSourceProvider(settings);
		var packageSources = packageSourceProvider.LoadPackageSources().Where(p => p.IsEnabled).ToList();

		// Each source's resources once, not once per package. A source that can't be reached is left out, rather than failing every package
		var sourcesResources = (await Task.WhenAll(packageSources.Select(source => GetSourceResources(source, cancellationToken))).ConfigureAwait(false))
			.OfType<SourceResources>()
			.ToList();

		// As dotnet list package --outdated does: be nice to HTTP sources, else bounded by the CPU
		var maxParallel = packageSources.Any(s => s.IsHttp) ? 8 : Environment.ProcessorCount + 1;
		var packagesResult = new IdePackageResult[installedPackages.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, installedPackages.Count), new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancellationToken }, async (index, ct) =>
		{
			var installedPackage = installedPackages[index];
			var isPrimarilyTransitive = installedPackage.ProjectPackageReferences.Any(p => p.IsTransitive is false) is false;
			var installedNugetPackageInfo = new InstalledNugetPackageInfo(isPrimarilyTransitive, installedPackage.ProjectPackageReferences);

			var latestFromSources = await Task.WhenAll(sourcesResources.Select(async resources => (resources.Source, Latest: await GetLatestPackageMetadata(resources, installedPackage.Name, ct).ConfigureAwait(false)))).ConfigureAwait(false);
			var packageFromSources = latestFromSources
				.Where(s => s.Latest is not null)
				.Select(s => new IdePackageFromSourceResult(s.Latest!, s.Source))
				.ToList();
			packagesResult[index] = new IdePackageResult(installedPackage.Name, packageFromSources, installedNugetPackageInfo);
		}).ConfigureAwait(false);
		return packagesResult.ToList();
	}

	private sealed record SourceResources(PackageSource Source, FindPackageByIdResource? FindPackageById, PackageMetadataResource? PackageMetadata);

	private static async Task<SourceResources?> GetSourceResources(PackageSource source, CancellationToken cancellationToken)
	{
		try
		{
			var repository = Repository.Factory.GetCoreV3(source);
			var findPackageById = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
			var packageMetadata = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
			return findPackageById is null && packageMetadata is null ? null : new SourceResources(source, findPackageById, packageMetadata);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}

	/// The latest version's metadata (latest stable, unless the package only has prereleases), or null if the source doesn't have the package.
	/// Finds the latest version from the version list (the flat container's small, CDN-cached index.json on V3 feeds), then asks for that one version's
	/// metadata - rather than the metadata of every version, which for a package with many versions is several registration pages
	private async Task<IPackageSearchMetadata?> GetLatestPackageMetadata(SourceResources resources, string packageId, CancellationToken cancellationToken)
	{
		try
		{
			if (resources.FindPackageById is not null && resources.PackageMetadata is not null)
			{
				var versions = (await resources.FindPackageById.GetAllVersionsAsync(packageId, _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false)).ToList();
				if (versions.Count is 0) return null;
				// _includePrerelease may be false, and this package may have only prerelease versions
				var latestVersion = versions.Where(v => _includePrerelease || v.IsPrerelease is false).Max() ?? versions.Max()!;
				var latest = await resources.PackageMetadata.GetMetadataAsync(new PackageIdentity(packageId, latestVersion), _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false);
				if (latest is not null) return latest;
				// The version list and the metadata can briefly disagree (e.g. just published) - fall through to reading every version's metadata
			}
			if (resources.PackageMetadata is null) return null;

			var foundPackages = (await resources.PackageMetadata.GetMetadataAsync(packageId, true, true, _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false)).ToList();
			return foundPackages.LastOrDefault(s => _includePrerelease || !s.Identity.Version.IsPrerelease) ?? foundPackages.LastOrDefault();
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// The source failed for this package - the others' results still stand
			return null;
		}
	}
}
