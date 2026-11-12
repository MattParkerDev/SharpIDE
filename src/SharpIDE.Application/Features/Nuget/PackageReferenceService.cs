using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.Application.Features.Nuget;

public sealed record PackageReferenceChangeResult(bool Success, string? Error, IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> Errors);

/// Adds, updates and removes a project's package references. SharpIDE.MsBuildHost writes the files where the project's evaluation keeps each version, then restores,
/// putting the files back when restore fails. The project is then re-evaluated, so it is current by the time this returns.
public class PackageReferenceService(BuildService buildService, ProjectEvaluationService projectEvaluationService, ILogger<PackageReferenceService> logger)
{
	private readonly BuildService _buildService = buildService;
	private readonly ProjectEvaluationService _projectEvaluationService = projectEvaluationService;
	private readonly ILogger<PackageReferenceService> _logger = logger;

	/// Adds the package at the version, or moves it to the version when the project references it already
	public Task<PackageReferenceChangeResult> AddOrUpdateAsync(SharpIdeProjectModel project, string packageId, NuGetVersion version, CancellationToken cancellationToken = default)
		=> ChangeAsync(project, $"Install {packageId} {version.ToNormalizedString()}", (rpc, ct) => rpc.AddOrUpdatePackageReferenceAsync(project.FilePath, packageId, version.ToNormalizedString(), ct), cancellationToken);

	public Task<PackageReferenceChangeResult> RemoveAsync(SharpIdeProjectModel project, string packageId, CancellationToken cancellationToken = default)
		=> ChangeAsync(project, $"Remove {packageId}", (rpc, ct) => rpc.RemovePackageReferenceAsync(project.FilePath, packageId, ct), cancellationToken);

	private async Task<PackageReferenceChangeResult> ChangeAsync(SharpIdeProjectModel project, string description, Func<IRpcBuildService, CancellationToken, Task<PackageReferenceChangeResultDto>> change, CancellationToken cancellationToken)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(PackageReferenceService)}.{nameof(ChangeAsync)}");
		PackageReferenceChangeResultDto result;
		try
		{
			result = await _buildService.ChangePackageReferenceAsync(change, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException ex)
		{
			// A build is already running
			return new PackageReferenceChangeResult(false, ex.Message, [], []);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "{Description} in {Project} failed", description, project.Name.Value);
			return new PackageReferenceChangeResult(false, ex.Message, [], []);
		}

		if (result.Success) _logger.LogInformation("{Description} in {Project}: wrote {Files}", description, project.Name.Value, string.Join(", ", result.ChangedFiles.Select(Path.GetFileName)));
		else _logger.LogWarning("{Description} in {Project} failed: {Error}", description, project.Name.Value, result.Error);

		// The host's evaluations still hold the files as they were - and on failure, the files may have been put back over a change made meanwhile
		if (result.ChangedFiles.Count > 0)
		{
			await _projectEvaluationService.ReloadForChangedFilesAsync([..result.ChangedFiles, project.FilePath], CancellationToken.None).ConfigureAwait(false);
		}
		return new PackageReferenceChangeResult(result.Success, result.Error, result.ChangedFiles, result.Errors);
	}
}
