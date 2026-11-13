using System.Runtime.InteropServices;
using SharpIDE.MsBuildHost.Contracts;
using SharpIDE.MsBuildHost.DesignTime;

namespace SharpIDE.MsBuildHost;

public class RpcDesignTimeBuildService : IRpcDesignTimeBuildService
{
	private readonly DesignTimeBuildManager _designTimeBuildManager = new();

	public Task<(string RuntimeVersion, string MsBuildSdkPath)> GetMsbuildInfoAsync() => Task.FromResult((RuntimeInformation.FrameworkDescription, SharpIdeMsbuildLocator.ResolvedMsBuildSdkPath));

	public Task<List<DesignTimeBuildResultDto>> DesignTimeBuildAsync(List<string> projectFilePaths, Dictionary<string, string> globalProperties, CancellationToken cancellationToken = default)
		=> _designTimeBuildManager.DesignTimeBuildAsync(projectFilePaths, globalProperties, cancellationToken);

	public Task<string?> TryGetProjectOutputPathAsync(string projectFilePath, CancellationToken cancellationToken = default)
		=> _designTimeBuildManager.TryGetProjectOutputPathAsync(projectFilePath, cancellationToken);
}
