using System.Runtime.InteropServices;
using SharpIDE.MsBuildHost.Contracts;
using SharpIDE.MsBuildHost.DesignTime;

namespace SharpIDE.MsBuildHost;

public class RpcDesignTimeBuildService : IRpcDesignTimeBuildService
{
	// Separate ProjectCollections (design-time builds' has their global properties), sharing the process-wide XML cache, so project files and SDK props/targets are parsed once
	private readonly DesignTimeBuildManager _designTimeBuildManager = new();
	private readonly ProjectEvaluator _projectEvaluator = new();

	public Task<(string RuntimeVersion, string MsBuildSdkPath)> GetMsbuildInfoAsync() => Task.FromResult((RuntimeInformation.FrameworkDescription, SharpIdeMsbuildLocator.ResolvedMsBuildSdkPath));

	public Task<List<DesignTimeBuildResultDto>> DesignTimeBuildAsync(List<string> projectFilePaths, Dictionary<string, string> globalProperties, CancellationToken cancellationToken = default)
		=> _designTimeBuildManager.DesignTimeBuildAsync(projectFilePaths, globalProperties, cancellationToken);

	public Task<string?> TryGetProjectOutputPathAsync(string projectFilePath, CancellationToken cancellationToken = default)
		=> _designTimeBuildManager.TryGetProjectOutputPathAsync(projectFilePath, cancellationToken);

	public Task<List<ProjectEvaluationDto>> EvaluateProjectsAsync(List<string> projectFilePaths, CancellationToken cancellationToken = default)
		=> _projectEvaluator.EvaluateProjectsAsync(projectFilePaths, cancellationToken);

	public Task<List<ProjectEvaluationDto>> ReloadProjectsAsync(List<string> changedFilePaths, CancellationToken cancellationToken = default)
		=> _projectEvaluator.ReloadProjectsAsync(changedFilePaths, cancellationToken);

	public Task UnloadAllProjectsAsync() => _projectEvaluator.UnloadAllProjectsAsync();

	public Task<string> GetOrCreateUserSecretsIdAsync(string projectFilePath) => _projectEvaluator.GetOrCreateUserSecretsIdAsync(projectFilePath);
}
