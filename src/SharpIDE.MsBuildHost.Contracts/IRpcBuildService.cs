using PolyType;
using StreamJsonRpc;

namespace SharpIDE.MsBuildHost.Contracts;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IRpcBuildService
{
	Task<bool> Ping();
	Task<(BuildResultDto buildResultDto, Exception? Exception)> MsBuildAsync(string solutionOrProjectFilePath, BuildTypeDto buildType = BuildTypeDto.Build, CancellationToken cancellationToken = default);
	Task BeginWritingMsBuildOutputToSocket(string unixDomainSocketFilePath);
	Task<(string RuntimeVersion, string MsBuildSdkPath)> GetMsbuildInfoAsync();

	/// Restores, then builds the project with Build;ComputeRunArguments (TargetFramework set as a global property when given), returning the run properties from its state after the build
	Task<RunBuildResultDto> BuildProjectForRunAsync(string projectFilePath, string? targetFramework, CancellationToken cancellationToken = default);

	// Evaluation only - these never run targets, so they can run alongside a build

	/// Evaluates (or re-evaluates, re-reading the project file from disk) each project, its outer project plus one evaluation per TFM
	Task<List<ProjectEvaluationDto>> EvaluateProjectsAsync(List<string> projectFilePaths, CancellationToken cancellationToken = default);
	/// Files that projects may import changed on disk (project files, Directory.Build.props/targets, Directory.Packages.props, obj/*.nuget.g.props/targets...).
	/// Reloads them, and returns the re-evaluation of every loaded project they may affect.
	Task<List<ProjectEvaluationDto>> ReloadProjectsAsync(List<string> changedFilePaths, CancellationToken cancellationToken = default);
	Task UnloadAllProjectsAsync();
	/// Adds a UserSecretsId property to the project file if it doesn't have one, and returns it
	Task<string> GetOrCreateUserSecretsIdAsync(string projectFilePath);
}
