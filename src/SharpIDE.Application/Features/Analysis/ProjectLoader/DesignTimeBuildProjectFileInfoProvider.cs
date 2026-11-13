using System.Collections.Immutable;
using Microsoft.CodeAnalysis.MSBuild;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.Application.Features.Analysis.ProjectLoader;

/// What the Worker asks for each project's design-time build results. Takes the place of Roslyn's BuildHostProjectFileInfoProvider (and its BuildHost process).
/// The Worker asks for one project at a time, so the projects it is going to ask for are design-time built up front, all together (in parallel, in one build session),
/// and served from those results - as Roslyn's HotReloadMSBuildWorkspace's provider serves already built project instances.
internal sealed class DesignTimeBuildProjectFileInfoProvider(
	DesignTimeBuildService designTimeBuildService,
	string solutionFilePath,
	ImmutableDictionary<string, string> globalProperties,
	ProjectFileExtensionRegistry projectFileExtensionRegistry,
	DiagnosticReporter diagnosticReporter,
	DesignTimeProjectMap? projectMap)
{
	private readonly Dictionary<string, DesignTimeBuildResultDto> _results = new(PathComparison.Comparer);

	/// Design-time builds the projects, and the projects they reference (transitively), for LoadProjectFileInfosAsync to serve.
	/// One batch per level of references not built yet - e.g. reloading a project also builds the projects it references, which the Worker will ask for.
	public async Task DesignTimeBuildAsync(IReadOnlyCollection<string> projectPaths, CancellationToken cancellationToken)
	{
		using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(DesignTimeBuildProjectFileInfoProvider)}.{nameof(DesignTimeBuildAsync)}");
		var pathsToBuild = GetPathsToBuild(projectPaths);
		while (pathsToBuild.Count is not 0)
		{
			var results = await designTimeBuildService.DesignTimeBuildAsync(solutionFilePath, pathsToBuild, new Dictionary<string, string>(globalProperties), cancellationToken).ConfigureAwait(false);
			foreach (var result in results) _results[result.ProjectFilePath] = result;
			pathsToBuild = GetPathsToBuild(results
				.SelectMany(result => result.ProjectFileInfos)
				.Where(info => info.FilePath is not null)
				.SelectMany(info => info.ProjectReferences.Select(reference => Path.GetFullPath(reference.Path, Path.GetDirectoryName(info.FilePath)!))));
		}
	}

	/// Not built yet, not already loaded (in the project map - the Worker uses those as they are), and in a language we load
	private List<string> GetPathsToBuild(IEnumerable<string> projectPaths) => projectPaths
		.Distinct(PathComparison.Comparer)
		.Where(path => _results.ContainsKey(path) is false)
		.Where(path => projectMap?.ContainsProjectInfosFor(path) is not true)
		.Where(path => projectFileExtensionRegistry.TryGetLanguageNameFromProjectPath(path, DiagnosticReportingMode.Ignore, out _, out _))
		.ToList();

	public async Task<ImmutableArray<ProjectFileInfoDto>> LoadProjectFileInfosAsync(string projectPath, DiagnosticReportingOptions reportingOptions, CancellationToken cancellationToken)
	{
		// Failure is reported by the registry
		if (projectFileExtensionRegistry.TryGetLanguageNameFromProjectPath(projectPath, reportingOptions.OnLoaderFailure, out _, out _) is false) return [];

		// A project not built up front - e.g. one outside the solution, referenced by one in it
		if (_results.ContainsKey(projectPath) is false) await DesignTimeBuildAsync([projectPath], cancellationToken).ConfigureAwait(false);
		if (_results.TryGetValue(projectPath, out var result) is false) return [];

		foreach (var diagnostic in result.Diagnostics)
		{
			// As Roslyn's DiagnosticReporter reports a BuildHost's log items
			diagnosticReporter.Report(DiagnosticReportingMode.Log, string.IsNullOrWhiteSpace(diagnostic.Message)
				? $"Msbuild failed when processing the file '{diagnostic.ProjectFilePath}'"
				: $"Msbuild failed when processing the file '{diagnostic.ProjectFilePath}' with message: {diagnostic.Message}");
		}
		return [.. result.ProjectFileInfos];
	}

	public async Task<ImmutableArray<string>> GetProjectOutputPathsAsync(string projectPath, CancellationToken cancellationToken)
	{
		var path = await designTimeBuildService.TryGetProjectOutputPathAsync(solutionFilePath, projectPath, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrEmpty(path) ? [] : [path];
	}
}
