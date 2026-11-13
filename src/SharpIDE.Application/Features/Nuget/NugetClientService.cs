using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using SharpIDE.Application.Features.Evaluation;

namespace SharpIDE.Application.Features.Nuget;

public record InstalledNugetPackageInfo(bool IsPrimarilyTransitive, List<ProjectPackageReference> ProjectPackageReferences);
public record IdePackageResult(string PackageId, List<IdePackageFromSourceResult> PackageFromSources, InstalledNugetPackageInfo? InstalledNugetPackageInfo);
/// The source's latest stable version's metadata (or latest prerelease's, if it only has prereleases), and its latest prerelease version if that's newer -
/// so the prerelease toggle is applied in memory, without asking the source again
public record struct IdePackageFromSourceResult(IPackageSearchMetadata PackageSearchMetadata, PackageSource Source, NuGetVersion? LatestPrereleaseVersion = null)
{
	public NuGetVersion GetLatestVersion(bool includePrerelease) =>
		includePrerelease && LatestPrereleaseVersion is { } latestPrerelease ? latestPrerelease : PackageSearchMetadata.Identity.Version;
}
public class NugetClientService
{
	private readonly SourceCacheContext _sourceCacheContext = new SourceCacheContext();
	private readonly ILogger _nugetLogger = NullLogger.Instance;
	/// The top 100 packages across the enabled sources, by download count, each with every source that has it. Yields a new snapshot each time it changes:
	/// sources' searches answer at very different speeds (nuget.org's in ~2s, an Azure DevOps feed's in 20s), so results are shown as they arrive.
	/// Whether the other sources have a package is looked up as soon as it makes the top 100 - a lookup is far faster than a slow source's search.
	/// Unlike the installed packages, this depends on includePrerelease - the source's search decides which packages are the top 100, and with prereleases,
	/// includes those with only prereleases
	public async IAsyncEnumerable<List<IdePackageResult>> GetTop100Results(string directoryPath, bool includePrerelease, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(NugetClientService)}.{nameof(GetTop100Results)}");
		var packageSources = LoadEnabledPackageSources(directoryPath);
		var sourcesResourcesTask = Task.WhenAll(packageSources.Select(source => GetSourceResources(source, cancellationToken)));

		// By package id, each source (by its index in packageSources, so in source order) that has it, with its metadata there. Only touched by this loop
		var found = new Dictionary<string, SortedDictionary<int, LatestFromSource>>(StringComparer.OrdinalIgnoreCase);
		var lookedUpPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Search for top packages (no search term = all), all sources at once
		var searches = packageSources.Select(async (source, index) => (SourceIndex: index, Results: await SearchTopPackages(source, includePrerelease, cancellationToken).ConfigureAwait(false))).ToList();
		var lookups = new List<Task<List<(string PackageId, int SourceIndex, LatestFromSource Latest)>>>();
		string? lastYieldedSignature = null;

		while (searches.Count + lookups.Count > 0)
		{
			var completed = await Task.WhenAny(searches.Cast<Task>().Concat(lookups)).ConfigureAwait(false);
			if (completed is Task<(int SourceIndex, IReadOnlyList<IPackageSearchMetadata> Results)> search && searches.Remove(search))
			{
				var (sourceIndex, results) = await search.ConfigureAwait(false);
				// A search result is the latest version the search allowed - so there's no newer prerelease to keep
				foreach (var metadata in results) AddFound(found, metadata.Identity.Id, sourceIndex, new LatestFromSource(metadata, null));
			}
			else
			{
				var lookup = (Task<List<(string PackageId, int SourceIndex, LatestFromSource Latest)>>)completed;
				lookups.Remove(lookup);
				foreach (var (packageId, sourceIndex, latest) in await lookup.ConfigureAwait(false)) AddFound(found, packageId, sourceIndex, latest);
			}

			var topPackages = GetTopPackages(found, packageSources);

			// Packages new to the top 100 - find out if the sources that haven't returned them have them too
			var toLookUp = topPackages
				.Where(p => lookedUpPackageIds.Add(p.PackageId))
				.Select(p => (p.PackageId, AlreadyFoundIn: found[p.PackageId].Keys.ToHashSet()))
				.ToList();
			if (toLookUp.Count > 0 && packageSources.Count > 1) lookups.Add(LookUpInOtherSources(toLookUp, sourcesResourcesTask, GetMaxParallel(packageSources), cancellationToken));

			// A slow source's search often changes nothing (its packages have no download count, so rarely make the top 100)
			var signature = string.Join('|', topPackages.Select(p => $"{p.PackageId}:{string.Join(',', found[p.PackageId].Keys)}"));
			if (signature == lastYieldedSignature) continue;
			lastYieldedSignature = signature;
			yield return topPackages;
		}
	}

	private static void AddFound(Dictionary<string, SortedDictionary<int, LatestFromSource>> found, string packageId, int sourceIndex, LatestFromSource latest)
	{
		if (found.TryGetValue(packageId, out var bySource) is false) found[packageId] = bySource = [];
		// A source's search result has the download count, so is kept over a lookup's
		bySource.TryAdd(sourceIndex, latest);
	}

	/// Ordered by download count (the highest any source reports), each package's sources in source order
	private static List<IdePackageResult> GetTopPackages(Dictionary<string, SortedDictionary<int, LatestFromSource>> found, List<PackageSource> packageSources) =>
		found
			.OrderByDescending(p => p.Value.Values.Max(m => m.Metadata.DownloadCount ?? 0))
			.Take(100)
			.Select(p => new IdePackageResult(
				p.Value.Values.First().Metadata.Identity.Id,
				p.Value.Select(s => new IdePackageFromSourceResult(s.Value.Metadata, packageSources[s.Key], s.Value.LatestPrereleaseVersion)).ToList(),
				null))
			.ToList();

	private async Task<List<(string PackageId, int SourceIndex, LatestFromSource Latest)>> LookUpInOtherSources(List<(string PackageId, HashSet<int> AlreadyFoundIn)> packages, Task<SourceResources?[]> sourcesResourcesTask, int maxParallel, CancellationToken cancellationToken)
	{
		var sourcesResources = await sourcesResourcesTask.ConfigureAwait(false);
		var foundInOtherSources = new ConcurrentBag<(string PackageId, int SourceIndex, LatestFromSource Latest)>();
		await Parallel.ForEachAsync(packages, new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancellationToken }, async (package, ct) =>
		{
			await Task.WhenAll(sourcesResources.Select(async (resources, sourceIndex) =>
			{
				if (resources is null || package.AlreadyFoundIn.Contains(sourceIndex)) return;
				var latest = await GetLatestPackageMetadata(resources, package.PackageId, ct).ConfigureAwait(false);
				if (latest is not null) foundInOtherSources.Add((package.PackageId, sourceIndex, latest.Value));
			})).ConfigureAwait(false);
		}).ConfigureAwait(false);
		return foundInOtherSources.ToList();
	}

	private async Task<IReadOnlyList<IPackageSearchMetadata>> SearchTopPackages(PackageSource source, bool includePrerelease, CancellationToken cancellationToken)
	{
		try
		{
			var repository = Repository.Factory.GetCoreV3(source);
			var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
			if (searchResource is null) return [];

			var results = await searchResource.SearchAsync(
				searchTerm: string.Empty,
				filters: new SearchFilter(includePrerelease: includePrerelease),
				skip: 0,
				take: 100,
				log: _nugetLogger,
				cancellationToken: cancellationToken).ConfigureAwait(false);
			return results.ToList();
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A source that can't be reached is left out, rather than failing the search
			return [];
		}
	}

	private static List<PackageSource> LoadEnabledPackageSources(string directoryPath)
	{
		var settings = Settings.LoadDefaultSettings(root: directoryPath);
		var packageSourceProvider = new PackageSourceProvider(settings);
		return packageSourceProvider.LoadPackageSources().Where(p => p.IsEnabled).ToList();
	}

	/// As dotnet list package --outdated does: be nice to HTTP sources, else bounded by the CPU
	private static int GetMaxParallel(List<PackageSource> packageSources) => packageSources.Any(s => s.IsHttp) ? 8 : Environment.ProcessorCount + 1;

	/// Every listed version, prereleases included, newest first - whether prereleases are shown is decided by the caller, in memory
	public async Task<List<IPackageSearchMetadata>> GetAllVersionsOfPackageInSource(string packageId, PackageSource source, CancellationToken cancellationToken = default)
	{
			var repository = Repository.Factory.GetCoreV3(source);
			var packageMetadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);

			var metadata = await packageMetadataResource.GetMetadataAsync(
				packageId, includePrerelease: true, includeUnlisted: false,
				_sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false);

			//var packageByIdResource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
			var metadataList = metadata.ToList();
			// Newest first - sorted here, as sources don't all return them in the same order (nuget.org's are oldest first, but that isn't guaranteed)
			return metadataList.OrderByDescending(m => m.Identity.Version).ToList();
	}

	/// Each version of the package restored to the global packages folder (as the directory's NuGet config sets it), newest first
	public List<(NuGetVersion Version, string Path)> GetLocalInstallations(string packageId, string directoryPath)
	{
		var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(root: directoryPath));
		// The folder layout is {id}/{version}, both lower case
		var packageFolder = Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant());
		if (Directory.Exists(packageFolder) is false) return [];
		return Directory.EnumerateDirectories(packageFolder)
			.Select(path => (Version: NuGetVersion.TryParse(Path.GetFileName(path), out var version) ? version : null, Path: path))
			.Where(i => i.Version is not null)
			.Select(i => (i.Version!, i.Path))
			.OrderByDescending(i => i.Item1)
			.ToList();
	}

	/// Every installed package, with the latest version each enabled source has. Yields a new snapshot (in installedPackages' order) each time it changes:
	/// first straight away with no sources' results (what's installed is known locally), then as the sources answer - sources answer at very different
	/// speeds (nuget.org in a second or two, an Azure DevOps feed in 20s), so a slow source doesn't hold back the others' results
	public async IAsyncEnumerable<List<IdePackageResult>> GetPackagesForInstalledPackages(string directoryPath, List<InstalledPackage> installedPackages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(NugetClientService)}.{nameof(GetPackagesForInstalledPackages)}");
		var packageSources = LoadEnabledPackageSources(directoryPath);

		var installedInfos = installedPackages
			.Select(p => new InstalledNugetPackageInfo(IsPrimarilyTransitive: p.ProjectPackageReferences.Any(r => r.IsTransitive is false) is false, p.ProjectPackageReferences))
			.ToList();
		// By installed package index, the latest from each source (by its index in packageSources, so in source order) that has it. Only touched by this method's loop
		var found = installedPackages.Select(_ => new SortedDictionary<int, LatestFromSource>()).ToList();
		List<IdePackageResult> Snapshot() => installedPackages
			.Select((p, index) => new IdePackageResult(p.Name, found[index].Select(s => new IdePackageFromSourceResult(s.Value.Metadata, packageSources[s.Key], s.Value.LatestPrereleaseVersion)).ToList(), installedInfos[index]))
			.ToList();

		yield return Snapshot();
		if (installedPackages.Count is 0 || packageSources.Count is 0) yield break;

		// Each source looks up every package on its own, so a slow source only delays its own results. Per source, as before it was per package
		var maxParallel = GetMaxParallel(packageSources);
		var channel = Channel.CreateUnbounded<(int PackageIndex, int SourceIndex, LatestFromSource Latest)>(new UnboundedChannelOptions { SingleReader = true });
		using var lookupsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var lookups = Task.WhenAll(packageSources.Select((source, sourceIndex) => Task.Run(async () =>
		{
			// A source that can't be reached is left out, rather than failing every package
			if (await GetSourceResources(source, lookupsCts.Token).ConfigureAwait(false) is not { } resources) return;
			await Parallel.ForEachAsync(Enumerable.Range(0, installedPackages.Count), new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = lookupsCts.Token }, async (packageIndex, ct) =>
			{
				var latest = await GetLatestPackageMetadata(resources, installedPackages[packageIndex].Name, ct).ConfigureAwait(false);
				if (latest is not null) channel.Writer.TryWrite((packageIndex, sourceIndex, latest.Value));
			}).ConfigureAwait(false);
		}, lookupsCts.Token)));
		_ = lookups.ContinueWith(t => channel.Writer.TryComplete(t.Exception), TaskScheduler.Default);

		try
		{
			// Results arrive a package at a time - gather those arriving close together into one snapshot, so the list isn't rebuilt for every package
			var batchWindow = TimeSpan.FromMilliseconds(250);
			while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var batchEnd = DateTime.UtcNow + batchWindow;
				var changed = false;
				while (true)
				{
					while (channel.Reader.TryRead(out var result)) changed |= found[result.PackageIndex].TryAdd(result.SourceIndex, result.Latest);
					var remaining = batchEnd - DateTime.UtcNow;
					if (remaining <= TimeSpan.Zero) break;
					using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					waitCts.CancelAfter(remaining);
					try
					{
						if (await channel.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false) is false) break; // Every source has answered
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
					{
						break; // The window ended
					}
				}
				if (changed) yield return Snapshot();
			}
		}
		finally
		{
			// Stopped early (cancelled, or the caller stopped enumerating) - don't leave the lookups running
			await lookupsCts.CancelAsync().ConfigureAwait(false);
		}
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

	/// The latest stable version's metadata (the latest prerelease's, if the package only has prereleases) and the latest prerelease version if it's newer -
	/// so whether prereleases are shown is decided in memory - or null if the source doesn't have the package.
	/// Finds the latest versions from the version list (the flat container's small, CDN-cached index.json on V3 feeds), then asks for one version's
	/// metadata - rather than the metadata of every version, which for a package with many versions is several registration pages
	private async Task<LatestFromSource?> GetLatestPackageMetadata(SourceResources resources, string packageId, CancellationToken cancellationToken)
	{
		try
		{
			if (resources.FindPackageById is not null && resources.PackageMetadata is not null)
			{
				var versions = (await resources.FindPackageById.GetAllVersionsAsync(packageId, _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false)).ToList();
				if (versions.Count is 0) return null;
				var (stableVersion, latestPrereleaseVersion) = GetLatestVersions(versions);
				var latest = await resources.PackageMetadata.GetMetadataAsync(new PackageIdentity(packageId, stableVersion), _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false);
				if (latest is not null) return new LatestFromSource(latest, latestPrereleaseVersion);
				// The version list and the metadata can briefly disagree (e.g. just published) - fall through to reading every version's metadata
			}
			if (resources.PackageMetadata is null) return null;

			var foundPackages = (await resources.PackageMetadata.GetMetadataAsync(packageId, true, true, _sourceCacheContext, _nugetLogger, cancellationToken).ConfigureAwait(false)).ToList();
			if (foundPackages.Count is 0) return null;
			var (stable, latestPrerelease) = GetLatestVersions(foundPackages.Select(p => p.Identity.Version).ToList());
			return new LatestFromSource(foundPackages.First(p => p.Identity.Version == stable), latestPrerelease);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// The source failed for this package - the others' results still stand
			return null;
		}
	}

	/// The latest stable version (the latest prerelease, if there are only prereleases), and the latest prerelease if it's newer than that
	private static (NuGetVersion Stable, NuGetVersion? LatestPrerelease) GetLatestVersions(List<NuGetVersion> versions)
	{
		var latest = versions.Max()!;
		var stable = versions.Where(v => v.IsPrerelease is false).Max() ?? latest;
		return (stable, latest > stable ? latest : null);
	}

	/// A source's latest stable version's metadata (or latest prerelease's, if it only has prereleases), and its latest prerelease version if newer
	private readonly record struct LatestFromSource(IPackageSearchMetadata Metadata, NuGetVersion? LatestPrereleaseVersion);
}
