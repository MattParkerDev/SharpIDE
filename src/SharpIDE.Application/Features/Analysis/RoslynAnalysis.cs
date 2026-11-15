using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.DecompiledSource;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.PdbSourceDocument;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Remote.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Remote.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.SignatureHelp;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.Analysis.ProjectLoader;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Application.Features.Analysis.WorkspaceServices;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.MsBuildHost.Contracts;
using VSInternalClientCapabilities = Roslyn.LanguageServer.Protocol.VSInternalClientCapabilities;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis(ILogger<RoslynAnalysis> logger, BuildService buildService, DesignTimeBuildService designTimeBuildService, AnalyzerFileWatcher analyzerFileWatcher)
{
	private readonly ILogger<RoslynAnalysis> _logger = logger;
	private readonly BuildService _buildService = buildService;
	private readonly DesignTimeBuildService _designTimeBuildService = designTimeBuildService;
	private readonly AnalyzerFileWatcher _analyzerFileWatcher = analyzerFileWatcher;

	public static AdhocWorkspace? _workspace;
	/// When each project's last design-time build started - the solution load's, for projects not reloaded since
	private DateTime _solutionDesignTimeBuildStartedUtc = DateTime.MaxValue;
	private readonly ConcurrentDictionary<string, DateTime> _projectDesignTimeBuildStartedUtc = new(PathComparison.Comparer);
	private static CustomMsBuildProjectLoader? _msBuildProjectLoader;
	private static RemoteSnapshotManager? _snapshotManager;
	private static RemoteSemanticTokensLegendService? _semanticTokensLegendService;
	private static ICodeFixService? _codeFixService;
	private static ICodeRefactoringService? _codeRefactoringService;
	private static IDocumentMappingService? _documentMappingService;
	private static IMetadataAsSourceFileService _metadataAsSourceFileService = null!;
	private static IImplementationAssemblyLookupService _implementationAssemblyLookupService = null!;
	private static SignatureHelpService _signatureHelpService = null!;

	// Primarily used for getting the globs for a project
	private Dictionary<ProjectId, ProjectFileInfoDto> _projectFileInfoMap = new();

	public TaskCompletionSource _solutionLoadedTcs = null!;
	private SharpIdeSolutionModel? _sharpIdeSolutionModel;
	public void StartLoadingSolutionInWorkspace(SharpIdeSolutionModel solutionModel)
	{
		_solutionLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = Task.Run(async () =>
		{
			try
			{
				CreateWorkspace();
				await LoadSolutionInWorkspace(solutionModel);
				await UpdateSolutionDiagnostics();
			}
			catch (Exception e)
			{
				_logger.LogError(e, "An error occurred during analysis");
			}
		});
	}
	public async Task LoadSolutionInWorkspace(SharpIdeSolutionModel solutionModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(LoadSolutionInWorkspace)}");
		_logger.LogInformation("RoslynAnalysis: Loading solution {SolutionPath}", solutionModel.FilePath);
		_sharpIdeSolutionModel = solutionModel;
		lock (_projectDiagnosticsCacheLock) _projectDiagnosticsCache.Clear(); // ProjectIds from a previous solution are meaningless now
		var timer = Stopwatch.StartNew();

		Guard.Against.Null(_workspace);

		await LoadSolutionCoreAsync(_sharpIdeSolutionModel.FilePath, cancellationToken);

		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Solution loaded in {ElapsedMilliseconds}ms", timer.ElapsedMilliseconds);
	}

	private async Task LoadSolutionCoreAsync(string solutionFilePath, CancellationToken cancellationToken)
	{
		// The design-time build host starts (on the first load) while the restore runs, rather than after it
		var startDesignTimeBuildHostTask = _designTimeBuildService.StartHostAsync(solutionFilePath, cancellationToken);
		using (var ___ = SharpIdeOtel.Source.StartActivity("RestoreSolution"))
		{
			// MsBuildProjectLoader doesn't do a restore which is absolutely required for resolving PackageReferences, if they have changed. I am guessing it just reads from project.assets.json
			// It is important to note that a Workspace has no concept of MSBuild, nuget packages etc. It is just told about project references and "metadata" references, which are dlls. This is what MSBuild does - it reads the csproj, and most importantly resolves nuget package references to dlls
			await _buildService.MsBuildAsync(solutionFilePath, BuildType.Restore, BuildStartedFlags.UserFacing, cancellationToken);
		}

		await startDesignTimeBuildHostTask;

		using (var ___ = SharpIdeOtel.Source.StartActivity("OpenSolution"))
		{
			//_msBuildProjectLoader!.LoadMetadataForReferencedProjects = true;

			// This call is the expensive part - design-time building every project
			_projectDesignTimeBuildStartedUtc.Clear();
			_solutionDesignTimeBuildStartedUtc = DateTime.UtcNow;
			var (solutionInfo, projectFileInfos) = await _msBuildProjectLoader!.LoadSolutionInfoAsync(solutionFilePath, cancellationToken: cancellationToken);

			var analyzerReferencePaths = solutionInfo.Projects
				.SelectMany(p => p.AnalyzerReferences.OfType<IsolatedAnalyzerFileReference>().Select(a => a.FullPath))
				.OfType<string>()
				.Distinct()
				.ToImmutableArray();
			await _analyzerFileWatcher.StartWatchingFiles(analyzerReferencePaths);

			// Atomic: re-run against the latest solution if another change lands first.
			// Reloading the same solution maps the loaded projects onto the existing ProjectIds and DocumentIds (by path), rather than replacing the solution (ClearSolution + AddSolution),
			// so unchanged projects and documents keep their state - parsed trees, compilations etc. Otherwise (the first load, or another solution), it's a new solution.
			var workspace = _workspace!;
			Dictionary<ProjectId, ProjectId> appliedProjectIdMap = [];
			workspace.SetCurrentSolution(
				currentSolution =>
				{
					if (currentSolution.ProjectIds.Count is 0 || PathComparison.PathEquals(currentSolution.FilePath, solutionInfo.FilePath) is false)
					{
						appliedProjectIdMap = [];
						using var _ = ArrayBuilder<ProjectInfo>.GetInstance(out var projectInfos);
						projectInfos.AddRange(solutionInfo.Projects);
						// The solution level analyzers are required for ICodeFixService to return fixes for compiler and IDE diagnostics
						return workspace.CreateSolution(solutionInfo)
							.WithAnalyzerReferences(SolutionLevelAnalyzerReferences.References)
							.AddProjects(projectInfos);
					}
					var (newSolution, projectIdMap) = ApplyLoadedProjectInfos(currentSolution, solutionInfo.Projects, removeOtherProjects: true);
					appliedProjectIdMap = projectIdMap;
					return newSolution;
				},
				changeKind: (oldSolution, _) => (oldSolution.ProjectIds.Count is 0 ? WorkspaceChangeKind.SolutionAdded : WorkspaceChangeKind.SolutionReloaded, null, null));
			// Keyed on the solution's ProjectIds, as GetProjectIdForNewFile looks them up
			_projectFileInfoMap = projectFileInfos.ToDictionary(pair => appliedProjectIdMap.GetValueOrDefault(pair.Key, pair.Key), pair => pair.Value);
		}

		_solutionLoadedTcs.SetResult();
	}

	private void CreateWorkspace()
	{
		using var __ = SharpIdeOtel.Source.StartActivity();

		var configuration = new ContainerConfiguration()
			.WithAssemblies(MefHostServices.DefaultAssemblies)
			.WithAssembly(typeof(RemoteSnapshotManager).Assembly)
			.WithPart<CSharpDecompilationService2>()
			.WithPart<DecompileWholeAssemblyToProjectMetadataAsSourceFileProvider>()
			.WithPart<PythiaStub>();

		// TODO: dispose container at some point?
		var container = configuration.CreateContainer();

		var host = MefHostServices.Create(container);
		_workspace = new AdhocWorkspace(host);
		_workspace.RegisterWorkspaceFailedHandler(o => _logger.LogError("WorkspaceFailedHandler - Workspace failure: {DiagnosticMessage}", o.Diagnostic.Message));

		var snapshotManager = container.GetExports<RemoteSnapshotManager>().FirstOrDefault();
		_snapshotManager = snapshotManager;

		_codeFixService = container.GetExports<ICodeFixService>().FirstOrDefault();
		_codeRefactoringService = container.GetExports<ICodeRefactoringService>().FirstOrDefault();
		_signatureHelpService = container.GetExports<SignatureHelpService>().FirstOrDefault()!;
		// TODO: Write an implementation of ISourceLinkService, as MS's implementation does not appear to be open source
		_metadataAsSourceFileService = container.GetExports<IMetadataAsSourceFileService>().FirstOrDefault()!;
		_implementationAssemblyLookupService = container.GetExports<IImplementationAssemblyLookupService>().FirstOrDefault()!;
		_semanticTokensLegendService = (RemoteSemanticTokensLegendService)container.GetExports<ISemanticTokensLegendService>().FirstOrDefault()!;
		_semanticTokensLegendService!.OnLspInitialized(new RemoteClientLSPInitializationOptions
		{
			ClientCapabilities = new VSInternalClientCapabilities(),
			TokenModifiers = TokenTypeProvider.ConstructTokenModifiers(),
			TokenTypes = TokenTypeProvider.ConstructTokenTypes(false)
		});
		_documentMappingService = container.GetExports<IDocumentMappingService>().FirstOrDefault();

		_msBuildProjectLoader = new CustomMsBuildProjectLoader(_workspace, _designTimeBuildService);
	}

	private async Task<Document> GetDocumentForSharpIdeFile(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		var textDocument = GetRequiredTextDocumentForSharpIdeFile(fileModel);
		return textDocument switch
		{
			Document document => document,
			_ => await GetRazorSourceGeneratedDocument(textDocument, cancellationToken)
		};
	}

	private static async Task<SourceGeneratedDocument> GetRazorSourceGeneratedDocument(TextDocument razorDocument, CancellationToken cancellationToken = default)
	{
		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(razorDocument.Project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(razorDocument);

		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		return generatedDocument;
	}

	/// The file's workspace document, found with Roslyn's file path index: a .cs file's Document, or a .razor/.cshtml file's AdditionalDocument.
	/// A file is in several workspace projects when its project multi-targets (one per target framework), or when it is linked into more than one
	/// project - this prefers the project whose folder contains the file, in its active target framework. Null if the file isn't in the workspace.
	private TextDocument? TryGetTextDocumentForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		if (sharpIdeFile.IsMetadataAsSourceFile)
		{
			var metadataAsSourceSolution = _metadataAsSourceFileService.TryGetWorkspace()?.CurrentSolution;
			var metadataAsSourceDocumentId = metadataAsSourceSolution?.GetDocumentIdsWithFilePath(sharpIdeFile.Path).SingleOrDefault();
			return metadataAsSourceDocumentId is null ? null : metadataAsSourceSolution!.GetDocument(metadataAsSourceDocumentId);
		}

		var solution = _workspace!.CurrentSolution;
		Func<DocumentId, bool> isExpectedKind = sharpIdeFile.IsCsharpFile ? solution.ContainsDocument : solution.ContainsAdditionalDocument;
		var documentIds = solution.GetDocumentIdsWithFilePath(sharpIdeFile.Path).Where(isExpectedKind).ToList();
		if (documentIds.Count is 0) return null;
		if (documentIds.Count is 1) return solution.GetTextDocument(documentIds[0]);

		var folderProjectFilePath = _sharpIdeSolutionModel!.GetProjectByContainingFolder(sharpIdeFile)?.FilePath;
		var best = documentIds.MaxBy(documentId =>
		{
			var project = solution.GetProject(documentId.ProjectId)!;
			return (PathComparison.PathEquals(project.FilePath, folderProjectFilePath) ? 2 : 0) + (IsActiveTargetFramework(project) ? 1 : 0);
		});
		return solution.GetTextDocument(best!);
	}

	private TextDocument GetRequiredTextDocumentForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		return TryGetTextDocumentForSharpIdeFile(sharpIdeFile) ?? throw new InvalidOperationException($"No document found in workspace for '{sharpIdeFile.Path}'");
	}

	private Document GetRequiredCSharpDocumentForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		return GetRequiredTextDocumentForSharpIdeFile(sharpIdeFile) as Document ?? throw new InvalidOperationException($"'{sharpIdeFile.Path}' is not a C# document");
	}

	/// The project a workspace project was loaded from. Several workspace projects map to one when it multi-targets.
	private SharpIdeProjectModel? GetSharpIdeProjectModelForProject(Project project) => _sharpIdeSolutionModel!.GetProjectByFilePath(project.FilePath);

	/// True if the project isn't multi-targeted, or this is the target framework selected in the IDE
	private bool IsActiveTargetFramework(Project project)
	{
		var flavor = project.State.NameAndFlavor.flavor;
		if (flavor is null) return true;
		var activeTargetFramework = GetSharpIdeProjectModelForProject(project)?.TargetFramework;
		return string.Equals(flavor, activeTargetFramework, StringComparison.OrdinalIgnoreCase);
	}

	private static Project GetProjectForSharpIdeProjectModel(SharpIdeProjectModel projectModel)
	{
		Guard.Against.Null(projectModel);
		var solution = _workspace!.CurrentSolution;
		var projectsForProjectPath = GetWorkspaceProjectsForFilePath(solution, projectModel.FilePath);
		if (projectsForProjectPath.Length is 0) throw new InvalidOperationException($"No project found in workspace for project path '{projectModel.FilePath}'");
		if (projectsForProjectPath.Length is 1)
		{
			return projectsForProjectPath[0];
		}

		var activeTargetFramework = projectModel.TargetFramework;
		return projectsForProjectPath.Single(project => string.Equals(project.State.NameAndFlavor.flavor, activeTargetFramework, StringComparison.OrdinalIgnoreCase));
	}

	/// Workspace project file path -> its ProjectIds (one per target framework), for the solution's ProjectIds list it was built from.
	/// Roslyn keeps that list instance while projects are only edited, and replaces it when one is added or removed - so the index is rebuilt only then,
	/// wherever in the workspace that happens.
	private sealed record WorkspaceProjectIndex(IReadOnlyList<ProjectId> ProjectIds, FrozenDictionary<string, ImmutableArray<ProjectId>> ProjectIdsByFilePath);
	private static volatile WorkspaceProjectIndex? _workspaceProjectIndex;

	private static ImmutableArray<Project> GetWorkspaceProjectsForFilePath(Solution solution, string projectFilePath)
	{
		var index = _workspaceProjectIndex;
		if (index is null || ReferenceEquals(index.ProjectIds, solution.ProjectIds) is false || TryGetProjects(index, out var projects) is false)
		{
			index = new WorkspaceProjectIndex(solution.ProjectIds, solution.Projects
				.Where(p => p.FilePath is not null)
				.GroupBy(p => p.FilePath!, PathComparison.Comparer)
				.ToFrozenDictionary(g => g.Key, g => g.Select(p => p.Id).ToImmutableArray(), PathComparison.Comparer));
			_workspaceProjectIndex = index;
			TryGetProjects(index, out projects);
		}
		return projects;

		// False if the index is stale - a project gone, or its path changed - without the list instance changing. Shouldn't happen, but cheap to check.
		bool TryGetProjects(WorkspaceProjectIndex index, out ImmutableArray<Project> projects)
		{
			projects = [];
			if (index.ProjectIdsByFilePath.TryGetValue(projectFilePath, out var projectIds) is false) return true;
			var builder = ImmutableArray.CreateBuilder<Project>(projectIds.Length);
			foreach (var projectId in projectIds)
			{
				var project = solution.GetProject(projectId);
				if (project is null || PathComparison.PathEquals(project.FilePath, projectFilePath) is false) return false;
				builder.Add(project);
			}
			projects = builder.MoveToImmutable();
			return true;
		}
	}
}
