using System.Collections.Immutable;
using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.Analysis.ProjectLoader;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.SolutionDiscovery;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public async Task ReloadSolution(CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadSolution)}");
		_logger.LogInformation("RoslynAnalysis: Reloading solution");
		await _solutionLoadedTcs.Task;
		_solutionLoadedTcs = new TaskCompletionSource();
		Guard.Against.Null(_workspace);
		Guard.Against.Null(_msBuildProjectLoader);

		await LoadSolutionCoreAsync(_sharpIdeSolutionModel!.FilePath, cancellationToken);

		_logger.LogInformation("RoslynAnalysis: Solution reloaded");
	}

	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public Task ReloadProject(SharpIdeProjectModel projectModel, CancellationToken cancellationToken = default) => ReloadProjects([projectModel], cancellationToken);

	/// Reloads several projects (and those depending on them) with one restore and one load - e.g. several csproj files changed by a git checkout
	public async Task ReloadProjects(IReadOnlyCollection<SharpIdeProjectModel> projectModels, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadProjects)}");
		_logger.LogInformation("RoslynAnalysis: Reloading projects {ProjectPaths}", string.Join(", ", projectModels.Select(p => p.FilePath)));
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(_workspace);
		Guard.Against.Null(_msBuildProjectLoader);

		await _buildService.MsBuildAsync(_sharpIdeSolutionModel!.FilePath, BuildType.Restore, BuildStartedFlags.Internal, cancellationToken);
		var __ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(CustomMsBuildProjectLoader)}.{nameof(CustomMsBuildProjectLoader.LoadProjectInfosAsync)}");

		var oldSolution = _workspace.CurrentSolution;

		// we can reliably rely on the Solution's graph of project inter-references, as a project has only been reloaded - no projects have been added or removed from the solution
		var dependencyGraph = oldSolution.GetProjectDependencyGraph();
		var projectPathsToReload = projectModels
			.Select(GetProjectForSharpIdeProjectModel)
			.SelectMany(project => dependencyGraph.GetProjectsThatTransitivelyDependOnThisProject(project.Id).Select(id => oldSolution.GetProject(id)!.FilePath!).Append(project.FilePath!))
			.Distinct()
			.ToList();
		// The solution's other projects are referenced as they are, rather than design-time built and loaded again. A reference to a project outside the solution is still loaded.
		var projectMap = DesignTimeProjectMap.Create(oldSolution, projectPathsToReload);
		var designTimeBuildStartedUtc = DateTime.UtcNow;
		foreach (var path in projectPathsToReload) _projectDesignTimeBuildStartedUtc[path] = designTimeBuildStartedUtc;
		var (loadedProjectInfos, projectFileInfos) = await _msBuildProjectLoader.LoadProjectInfosAsync(projectPathsToReload.ToImmutableArray(), projectMap, null, cancellationToken);
		__?.Dispose();

		using var ___ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.Workspace.UpdateSolution");

		// Atomic: the transformation is re-run against the latest solution if another change (an edit, a file operation, another reload) lands first, so it can't be lost.
		// The loaded ProjectInfos have new ProjectIds and DocumentIds - they're mapped (by path) onto the solution's existing ones, so its projects and documents keep their state.
		// Expected to be a subset of the solution's projects, as only projects have been reloaded. A project that isn't (e.g. a newly referenced one outside the solution) is added.
		Dictionary<ProjectId, ProjectId> appliedProjectIdMap = [];
		_workspace.SetCurrentSolution(
			currentSolution =>
			{
				var (newSolution, projectIdMap) = ApplyLoadedProjectInfos(currentSolution, loadedProjectInfos, removeOtherProjects: false);
				appliedProjectIdMap = projectIdMap;
				return newSolution;
			},
			// Raised once the update is applied - by which point appliedProjectIdMap is the applied run's
			changeKind: (_, _) => loadedProjectInfos.Length is 1
				? (WorkspaceChangeKind.ProjectReloaded, appliedProjectIdMap[loadedProjectInfos[0].Id], null)
				: (WorkspaceChangeKind.SolutionChanged, null, null));

		// Keyed on the solution's ProjectIds, as GetProjectIdForNewFile looks them up
		foreach (var (loadedProjectId, projectFileInfo) in projectFileInfos)
		{
			_projectFileInfoMap[appliedProjectIdMap.GetValueOrDefault(loadedProjectId, loadedProjectId)] = projectFileInfo;
		}

		_workspace.UpdateReferencesAfterAdd();
		_logger.LogInformation("RoslynAnalysis: Project reloaded");
	}

	/// The loaded projects applied to the solution - with the loaded ProjectInfos' ProjectIds (and DocumentIds, and project references) mapped onto the solution's existing ones, by path,
	/// so unchanged projects and documents keep their state (and Roslyn's caches of it). A project not in the solution is added; with removeOtherProjects (a solution reload), solution projects not loaded are removed.
	/// A document that is still there keeps its state - text (including an open file's unsaved editor text), parsed tree. Disk changes reach the workspace through the file watcher
	/// (ExternalFilesChanged updates closed documents straight away, before a batch's reloads run), so its text is already current.
	/// Returns the loaded ProjectIds mapped to the solution's ProjectIds they were applied to.
	private static (Solution Solution, Dictionary<ProjectId, ProjectId> ProjectIdMap) ApplyLoadedProjectInfos(Solution solution, IReadOnlyList<ProjectInfo> loadedProjectInfos, bool removeOtherProjects)
	{
		var existingProjectIds = solution.Projects.GroupBy(static p => (p.FilePath!, p.Name)).ToDictionary(static g => g.Key, static g => g.First().Id);
		// A project not in the solution keeps its loaded ProjectId
		var projectIdMap = loadedProjectInfos.ToDictionary(static info => info.Id, info => existingProjectIds.GetValueOrDefault((info.FilePath!, info.Name)) ?? info.Id);

		var newSolution = solution;
		if (removeOtherProjects)
		{
			var loadedIds = projectIdMap.Values.ToHashSet();
			newSolution = newSolution.RemoveProjects([.. solution.ProjectIds.Where(id => loadedIds.Contains(id) is false)]);
		}
		foreach (var loadedProjectInfo in loadedProjectInfos)
		{
			Contract.ThrowIfNull(loadedProjectInfo.FilePath);
			var projectId = projectIdMap[loadedProjectInfo.Id];
			if (newSolution.ContainsProject(projectId) is false)
			{
				newSolution = newSolution.AddProject(loadedProjectInfo.WithProjectReferences(loadedProjectInfo.ProjectReferences.Select(MapProjectReference)));
				continue;
			}

			newSolution = newSolution.WithProjectInfo(ProjectInfo.Create(
					projectId,
					loadedProjectInfo.Version,
					loadedProjectInfo.Name,
					loadedProjectInfo.AssemblyName,
					loadedProjectInfo.Language,
					loadedProjectInfo.FilePath,
					loadedProjectInfo.OutputFilePath,
					loadedProjectInfo.CompilationOptions,
					loadedProjectInfo.ParseOptions,
					MapDocuments(projectId, loadedProjectInfo.Documents),
					loadedProjectInfo.ProjectReferences.Select(MapProjectReference),
					loadedProjectInfo.MetadataReferences,
					loadedProjectInfo.AnalyzerReferences,
					MapDocuments(projectId, loadedProjectInfo.AdditionalDocuments),
					isSubmission: false,
					hostObjectType: null,
					outputRefFilePath: loadedProjectInfo.OutputRefFilePath)
				.WithChecksumAlgorithm(loadedProjectInfo.ChecksumAlgorithm)
				.WithAnalyzerConfigDocuments(MapDocuments(projectId, loadedProjectInfo.AnalyzerConfigDocuments))
				.WithCompilationOutputInfo(loadedProjectInfo.CompilationOutputInfo));
		}
		return (newSolution, projectIdMap);

		// Only C# and VB projects are loaded, so a reference can be to a project that isn't - it keeps its ProjectId
		ProjectReference MapProjectReference(ProjectReference reference) => new(
			projectId: projectIdMap.GetValueOrDefault(reference.ProjectId, reference.ProjectId),
			aliases: reference.Aliases,
			embedInteropTypes: reference.EmbedInteropTypes);

		ImmutableArray<DocumentInfo> MapDocuments(ProjectId projectId, IReadOnlyList<DocumentInfo> documents) => documents.Select(documentInfo =>
		{
			// The solution's document at the same path, or a new one (e.g. a file added to the project)
			var existingDocumentId = solution.GetDocumentIdsWithFilePath(documentInfo.FilePath).FirstOrDefault(id => id.ProjectId == projectId);
			if (existingDocumentId is null) return documentInfo.WithId(DocumentId.CreateNewId(projectId));
			var existingDocument = solution.GetTextDocument(existingDocumentId);
			// The same text source is a no-op for WithProjectInfo, so the document's state is kept
			return existingDocument is null
				? documentInfo.WithId(existingDocumentId)
				: documentInfo.WithId(existingDocumentId).WithTextLoader(existingDocument.State.TextAndVersionSource.TextLoader);
		}).ToImmutableArray();
	}

	/// Whether the project is loaded in the workspace, and its last design-time build started before the given time - so hasn't seen changes made then
	public bool IsProjectDesignTimeBuiltBefore(SharpIdeProjectModel projectModel, DateTime utcTime)
	{
		if (_workspace is null || _workspace.CurrentSolution.Projects.Any(p => PathComparison.PathEquals(p.FilePath, projectModel.FilePath)) is false) return false;
		return _projectDesignTimeBuildStartedUtc.GetValueOrDefault(projectModel.FilePath, _solutionDesignTimeBuildStartedUtc) < utcTime;
	}

	public async Task<bool> ReloadProjectsWithAnyOfAnalyzerFileReferences(ImmutableArray<string> analyzerFilePaths, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadProjectsWithAnyOfAnalyzerFileReferences)}");
		await _solutionLoadedTcs.Task;
		var projectsToReload = _workspace!.CurrentSolution.Projects
			.Where(p => p.AnalyzerReferences
				.OfType<IsolatedAnalyzerFileReference>()
				.Where(s => s.FullPath is not null)
				.Any(a => analyzerFilePaths.Contains(a.FullPath!)))
			.ToList();

		if (projectsToReload.Count is 0) return false;

		_logger.LogInformation("RoslynAnalysis: Reloading {ProjectCount} projects that reference an analyzer that changed", projectsToReload.Count);
		foreach (var project in projectsToReload)
		{
			var sharpIdeProjectModel = GetSharpIdeProjectModelForProject(project);
			Guard.Against.Null(sharpIdeProjectModel);
			await ReloadProject(sharpIdeProjectModel, cancellationToken);
		}

		return true;
	}
}
