using PolyType;
using StreamJsonRpc;

namespace SharpIDE.MsBuildHost.Contracts;

/// Served by SharpIDE.MsBuildHost started with --design-time - a second, long-lived host per solution, so design-time builds and evaluation never wait on (or are cancelled with) a user's build.
/// Design-time builds and evaluation have a ProjectCollection each, sharing the process-wide XML cache.
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

	// Evaluation only - these never run targets, so they can run alongside a design-time build

	/// Evaluates (or re-evaluates, re-reading the project file from disk) each project, its outer project plus one evaluation per TFM
	Task<List<ProjectEvaluationDto>> EvaluateProjectsAsync(List<string> projectFilePaths, CancellationToken cancellationToken = default);
	/// Files that projects may import changed on disk (project files, Directory.Build.props/targets, Directory.Packages.props, obj/*.nuget.g.props/targets...).
	/// Reloads them, and returns the re-evaluation of every loaded project they may affect.
	Task<List<ProjectEvaluationDto>> ReloadProjectsAsync(List<string> changedFilePaths, CancellationToken cancellationToken = default);
	Task UnloadAllProjectsAsync();
	/// Adds a UserSecretsId property to the project file if it doesn't have one, and returns it
	Task<string> GetOrCreateUserSecretsIdAsync(string projectFilePath);
}
