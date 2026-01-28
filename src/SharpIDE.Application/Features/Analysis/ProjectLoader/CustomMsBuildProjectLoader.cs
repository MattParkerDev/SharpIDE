using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Collections.Immutable;

namespace SharpIDE.Application.Features.Analysis.ProjectLoader;
// I really don't like having to duplicate this, but we need to use IAnalyzerAssemblyLoaderProvider rather than IAnalyzerService,
// so that analyzers are shadow copied to prevent locking.
// My attempts to provide a custom IAnalyzerService to the MEF composition were in vain.
// I think this will only be temporary, as I think a more sophisticated ProjectLoader mechanism is going to be necessary.
// see roslyn LanguageServerProjectLoader, LanguageServerProjectSystem, ProjectSystemProjectFactory
// https://github.com/dotnet/roslyn/blob/52d073ff6f1c668e858bed838712467afcf83876/src/Workspaces/MSBuild/Core/MSBuild/MSBuildProjectLoader.cs
public partial class CustomMsBuildProjectLoader(Workspace workspace, ImmutableDictionary<string, string>? properties = null) : MSBuildProjectLoader(workspace, properties)
{
	public async Task<(ImmutableArray<ProjectInfo>, Dictionary<ProjectId, ProjectFileInfo>)> LoadProjectInfosAsync(
		List<string> projectFilePaths,
		ProjectMap? projectMap = null,
		IProgress<ProjectLoadProgress>? progress = null,
#pragma warning disable IDE0060 // TODO: decide what to do with this unusued ILogger, since we can't reliabily use it if we're sending builds out of proc
		ILogger? msbuildLogger = null,
#pragma warning restore IDE0060
		CancellationToken cancellationToken = default)
	{
		if (projectFilePaths.Count is 0)
		{
			throw new ArgumentException("At least one project file path must be specified.", nameof(projectFilePaths));
		}

		var requestedProjectOptions = DiagnosticReportingOptions.ThrowForAll;

		var reportingMode = GetReportingModeForUnrecognizedProjects();

		var discoveredProjectOptions = new DiagnosticReportingOptions(
			onPathFailure: reportingMode,
			onLoaderFailure: reportingMode);

		var buildHostProcessManager = new BuildHostProcessManager(Properties, loggerFactory: _loggerFactory);
		await using var _ = buildHostProcessManager.ConfigureAwait(false);

		var worker = new CustomWorker(
			_solutionServices,
			_diagnosticReporter,
			_pathResolver,
			_projectFileExtensionRegistry,
			buildHostProcessManager,
			requestedProjectPaths: projectFilePaths.ToImmutableArray(),
			baseDirectory: Directory.GetCurrentDirectory(),
			projectMap,
			progress,
			requestedProjectOptions,
			discoveredProjectOptions,
			this.LoadMetadataForReferencedProjects);

		var allProjectInfos = ImmutableArray.CreateBuilder<ProjectInfo>();
		var allFileInfoMap = new Dictionary<ProjectId, ProjectFileInfo>();

		await foreach (var (projectInfos, fileInfoMap) in worker.LoadInShardsAsync(cancellationToken).ConfigureAwait(false))
		{
			allProjectInfos.AddRange(projectInfos);
			foreach (var kvp in fileInfoMap)
			{
				allFileInfoMap.TryAdd(kvp.Key, kvp.Value);
			}
		}

		return (allProjectInfos.ToImmutable(), allFileInfoMap);
	}

	/// <summary>
	/// Loads the <see cref="SolutionInfo"/> for the specified solution file, including all projects referenced by the solution file and
	/// all the projects referenced by the project files.
	/// </summary>
	/// <param name="solutionFilePath">The path to the solution file to be loaded. This may be an absolute path or a path relative to the
	/// current working directory.</param>
	/// <param name="progress">An optional <see cref="IProgress{T}"/> that will receive updates as the solution is loaded.</param>
	/// <param name="msbuildLogger">An optional <see cref="ILogger"/> that will log MSBuild results.</param>
	/// <param name="cancellationToken">An optional <see cref="CancellationToken"/> to allow cancellation of this operation.</param>
	public new async Task<(SolutionInfo, Dictionary<ProjectId, ProjectFileInfo>)> LoadSolutionInfoAsync(
		string solutionFilePath,
		IProgress<ProjectLoadProgress>? progress = null,
		ILogger? msbuildLogger = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(solutionFilePath);

		var reportingMode = GetReportingModeForUnrecognizedProjects();

		var reportingOptions = new DiagnosticReportingOptions(
			onPathFailure: reportingMode,
			onLoaderFailure: reportingMode);

		var (absoluteSolutionPath, projects) = await SolutionFileReader.ReadSolutionFileAsync(solutionFilePath, _pathResolver, reportingMode, cancellationToken).ConfigureAwait(false);
		var projectPaths = projects.SelectAsArray(p => p.ProjectPath);

		using (_dataGuard.DisposableWait(cancellationToken))
		{
			SetSolutionProperties(absoluteSolutionPath);
		}

		IBinLogPathProvider binLogPathProvider = null!; // TODO: Fix

		var buildHostProcessManager = new BuildHostProcessManager(Properties, binLogPathProvider, _loggerFactory);
		await using var _ = buildHostProcessManager.ConfigureAwait(false);

		var worker = new CustomWorker(
			_solutionServices,
			_diagnosticReporter,
			_pathResolver,
			_projectFileExtensionRegistry,
			buildHostProcessManager,
			projectPaths,
			// TryGetAbsoluteSolutionPath should not return an invalid path
			baseDirectory: Path.GetDirectoryName(absoluteSolutionPath)!,
			projectMap: null,
			progress,
			requestedProjectOptions: reportingOptions,
			discoveredProjectOptions: reportingOptions,
			preferMetadataForReferencesOfDiscoveredProjects: false);

		var allSolutionInfos = ImmutableArray.CreateBuilder<ProjectInfo>();
		var allFileInfoMap = new Dictionary<ProjectId, ProjectFileInfo>();

		await foreach (var (projectInfos, fileInfoMap) in worker.LoadInShardsAsync(cancellationToken).ConfigureAwait(false))
		{
			allSolutionInfos.AddRange(projectInfos);
			foreach (var kvp in fileInfoMap)
			{
				allFileInfoMap.TryAdd(kvp.Key, kvp.Value);
			}
		}

		// construct workspace from loaded project infos
		var solutionInfo = SolutionInfo.Create(
			SolutionId.CreateNewId(debugName: absoluteSolutionPath),
			version: default,
			absoluteSolutionPath,
			allSolutionInfos);

		return (solutionInfo, allFileInfoMap);
	}
}
