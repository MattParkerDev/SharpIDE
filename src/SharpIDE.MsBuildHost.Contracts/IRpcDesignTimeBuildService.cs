using PolyType;
using StreamJsonRpc;

namespace SharpIDE.MsBuildHost.Contracts;

/// Served by SharpIDE.MsBuildHost started with --design-time - a second, long-lived host per solution, so design-time builds never wait on (or are cancelled with) a user's build.
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IRpcDesignTimeBuildService
{
	Task<(string RuntimeVersion, string MsBuildSdkPath)> GetMsbuildInfoAsync();

	/// Evaluates and design-time builds the projects (one build session for the batch, so no results outlive it), in parallel.
	/// Each project file is read from disk. globalProperties are added to the design-time build's own (e.g. SolutionDir).
	Task<List<DesignTimeBuildResultDto>> DesignTimeBuildAsync(List<string> projectFilePaths, Dictionary<string, string> globalProperties, CancellationToken cancellationToken = default);

	/// The project's TargetPath, from an evaluation - for projects that can't be design-time built (e.g. an F# project referenced by a C# one)
	Task<string?> TryGetProjectOutputPathAsync(string projectFilePath, CancellationToken cancellationToken = default);
}
