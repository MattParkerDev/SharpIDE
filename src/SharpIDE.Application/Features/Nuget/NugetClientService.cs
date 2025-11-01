using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Nuget;

//public record IdePackageResult(IPackageSearchMetadata PackageSearchMetadata, List<PackageSource> PackageSources);
public record IdePackageResult(string PackageId, List<IdePackageFromSourceResult> PackageFromSources);
public record struct IdePackageFromSourceResult(IPackageSearchMetadata PackageSearchMetadata, PackageSource Source);
public class NugetClientService
{
	private readonly bool _includePrerelease = false;
	public async Task<List<IdePackageResult>> GetTop100Results(string directoryPath, CancellationToken cancellationToken = default)
	{
		var settings = Settings.LoadDefaultSettings(root: directoryPath);
		var packageSourceProvider = new PackageSourceProvider(settings);
		var packageSources = packageSourceProvider.LoadPackageSources().Where(p => p.IsEnabled).ToList();

		var logger = NullLogger.Instance;
		var cache = new SourceCacheContext();

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
				log: logger,
				cancellationToken: cancellationToken).ConfigureAwait(false);

			packagesResult.AddRange(results.Select(s => new IdePackageResult(s.Identity.Id, [new IdePackageFromSourceResult(s, source)])));
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
					log: logger,
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
}
