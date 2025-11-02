using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Nuget;

public record InstalledNugetPackageInfo(bool IsTransitive, NuGetVersion Version);
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

			packagesResult.AddRange(results.Select(s => new IdePackageResult(s.Identity.Id, [new IdePackageFromSourceResult(s, source)], null)));
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

	public async Task<List<IdePackageResult>> GetPackagesForInstalledPackages(string directoryPath, List<ProjectEvaluation.InstalledPackage> installedPackages, CancellationToken cancellationToken = default)
	{
		var settings = Settings.LoadDefaultSettings(root: directoryPath);
		var packageSourceProvider = new PackageSourceProvider(settings);
		var packageSources = packageSourceProvider.LoadPackageSources().Where(p => p.IsEnabled).ToList();

		var packagesResult = new List<IdePackageResult>();
		foreach (var installedPackage in installedPackages)
		{
			var isTransitive = installedPackage.IsTopLevel is false;
			var nugetVersionString = installedPackage.ResolvedVersion ?? installedPackage.RequestedVersion;
			var nugetVersion = NuGetVersion.Parse(nugetVersionString);

			var installedNugetPackageInfo = new InstalledNugetPackageInfo(isTransitive, nugetVersion);
			var idePackageResult = new IdePackageResult(installedPackage.Name, [], installedNugetPackageInfo);

			var packageIdentity = new PackageIdentity(installedPackage.Name, nugetVersion);

			foreach (var source in packageSources)
			{
				var repository = Repository.Factory.GetCoreV3(source.Source);
				var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);

				if (metadataResource == null)
					continue;

				var foundPackages = await metadataResource.GetMetadataAsync(installedPackage.Name, _includePrerelease, true, _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false);
				var latestPackage = foundPackages.LastOrDefault();
				if (latestPackage != null)
				{
					idePackageResult.PackageFromSources.Add(new IdePackageFromSourceResult(latestPackage, source));
				}
			}

			packagesResult.Add(idePackageResult);
		}
		return packagesResult;
	}
}
