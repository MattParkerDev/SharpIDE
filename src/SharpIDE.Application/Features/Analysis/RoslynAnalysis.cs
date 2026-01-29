using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Diagnostics;
using System.Text;
using Ardalis.GuardClauses;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Remote.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using NuGet.Frameworks;
using SharpIDE.Application.Features.Analysis.FixLoaders;
using SharpIDE.Application.Features.Analysis.ProjectLoader;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using static Roslyn.Utilities.EnumerableExtensions;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using CompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using CompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using CompletionOptions = Microsoft.CodeAnalysis.Completion.CompletionOptions;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using VSInternalClientCapabilities = Roslyn.LanguageServer.Protocol.VSInternalClientCapabilities;

namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis(ILogger<RoslynAnalysis> logger, BuildService buildService, AnalyzerFileWatcher analyzerFileWatcher)
{
	private readonly ILogger<RoslynAnalysis> _logger = logger;
	private readonly BuildService _buildService = buildService;
	private readonly AnalyzerFileWatcher _analyzerFileWatcher = analyzerFileWatcher;

	public static AdhocWorkspace? _workspace;
	private static CustomMsBuildProjectLoader? _msBuildProjectLoader;
	private static RemoteSnapshotManager? _snapshotManager;
	private static RemoteSemanticTokensLegendService? _semanticTokensLegendService;
	private static ICodeFixService? _codeFixService;
	private static ICodeRefactoringService? _codeRefactoringService;
	private static IDocumentMappingService? _documentMappingService;
	private static HashSet<CodeRefactoringProvider> _codeRefactoringProviders = [];
	private static HashSet<CodeFixProvider> _codeFixProviders = [];

	// Primarily used for getting the globs for a project
	private Dictionary<ProjectId, ProjectFileInfo> _projectFileInfoMap = new();

	public TaskCompletionSource _solutionLoadedTcs = null!;
	private SharpIdeSolutionModel? _sharpIdeSolutionModel;
	public void StartLoadingSolutionInWorkspace(SharpIdeSolutionModel solutionModel)
	{
		_solutionLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = Task.Run(async () =>
		{
			try
			{
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
		var timer = Stopwatch.StartNew();
		if (_workspace is null)
		{
			using var __ = SharpIdeOtel.Source.StartActivity("CreateWorkspace");
			var configuration = new ContainerConfiguration()
				.WithAssemblies(MefHostServices.DefaultAssemblies)
				.WithAssembly(typeof(RemoteSnapshotManager).Assembly);

			// TODO: dispose container at some point?
			var container = configuration.CreateContainer();

			var host = MefHostServices.Create(container);
			_workspace = new AdhocWorkspace(host);
			_workspace.RegisterWorkspaceFailedHandler(o => _logger.LogError("WorkspaceFailedHandler - Workspace failure: {DiagnosticMessage}", o.Diagnostic.Message));

			var snapshotManager = container.GetExports<RemoteSnapshotManager>().FirstOrDefault();
			_snapshotManager = snapshotManager;

			_codeFixService = container.GetExports<ICodeFixService>().FirstOrDefault();
			_codeRefactoringService = container.GetExports<ICodeRefactoringService>().FirstOrDefault();

			_semanticTokensLegendService = (RemoteSemanticTokensLegendService)container.GetExports<ISemanticTokensLegendService>().FirstOrDefault()!;
			_semanticTokensLegendService!.OnLspInitialized(new RemoteClientLSPInitializationOptions
			{
				ClientCapabilities = new VSInternalClientCapabilities(),
				TokenModifiers = TokenTypeProvider.ConstructTokenModifiers(),
				TokenTypes = TokenTypeProvider.ConstructTokenTypes(false)
			});
			_documentMappingService = container.GetExports<IDocumentMappingService>().FirstOrDefault();

			_msBuildProjectLoader = new CustomMsBuildProjectLoader(_workspace);
		}

		using (var ___ = SharpIdeOtel.Source.StartActivity("RestoreSolution"))
		{
			// MsBuildProjectLoader doesn't do a restore which is absolutely required for resolving PackageReferences, if they have changed. I am guessing it just reads from project.assets.json
			await _buildService.MsBuildAsync(_sharpIdeSolutionModel.FilePath, BuildType.Restore, BuildStartedFlags.UserFacing, cancellationToken);
		}
		using (var ___ = SharpIdeOtel.Source.StartActivity("OpenSolution"))
		{
			//_msBuildProjectLoader!.LoadMetadataForReferencedProjects = true;
			var (solutionInfo, projectFileInfos) = await _msBuildProjectLoader!.LoadSolutionInfoAsync(_sharpIdeSolutionModel.FilePath, cancellationToken: cancellationToken);
			_projectFileInfoMap = projectFileInfos;
			var analyzerReferencePaths = solutionInfo.Projects
				.SelectMany(p => p.AnalyzerReferences.OfType<IsolatedAnalyzerFileReference>().Select(a => a.FullPath))
				.OfType<string>()
				.Distinct()
				.ToImmutableArray();

			await _analyzerFileWatcher.StartWatchingFiles(analyzerReferencePaths);
			_workspace.ClearSolution();
			var solution = _workspace.AddSolution(solutionInfo);

			// If these aren't added, IDiagnosticAnalyzerService will not return compiler analyzer diagnostics
			// Note that we aren't currently using IDiagnosticAnalyzerService
			//var solutionAnalyzerReferences = CreateSolutionLevelAnalyzerReferencesForWorkspace(_workspace);
			//solution = solution.WithAnalyzerReferences(solutionAnalyzerReferences);
			//_workspace.SetCurrentSolution(solution);
		}
		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Solution loaded in {ElapsedMilliseconds}ms", timer.ElapsedMilliseconds);
		_solutionLoadedTcs.SetResult();

		using (var ____ = SharpIdeOtel.Source.StartActivity("LoadAnalyzersAndFixers"))
		{
			foreach (var assembly in MefHostServices.DefaultAssemblies)
			{
				// These could be loaded from the composition via _workspace.CurrentSolution.Services.ExportProvider.GetExports<Lazy<CodeFixProvider, CodeChangeProviderMetadata>>().ToList(),
				// however we need all the CodeFixProviders/CodeRefactoringProviders immediately on the first code action request, so I would prefer to do it here
				var fixers = CodeFixProviderLoader.LoadCodeFixProviders([assembly], LanguageNames.CSharp);
				_codeFixProviders.AddRange(fixers);
				var refactoringProviders = CodeRefactoringProviderLoader.LoadCodeRefactoringProviders([assembly], LanguageNames.CSharp);
				_codeRefactoringProviders.AddRange(refactoringProviders);
			}
			_codeRefactoringProviders = _codeRefactoringProviders.DistinctBy(s => s.GetType().Name).ToHashSet();
			_codeFixProviders = _codeFixProviders.DistinctBy(s => s.GetType().Name).ToHashSet();
		}

		// // TODO: Distinct on the assemblies first
		// foreach (var project in solution.Projects)
		// {
		// 	var relevantAnalyzerReferences = project.AnalyzerReferences.OfType<AnalyzerFileReference>().ToArray();
		// 	var assemblies = relevantAnalyzerReferences.Select(a => a.GetAssembly()).ToArray();
		// 	var language = project.Language;
		// 	//var analyzers = relevantAnalyzerReferences.SelectMany(a => a.GetAnalyzers(language));
		// 	var fixers = CodeFixProviderLoader.LoadCodeFixProviders(assemblies, language);
		// 	_codeFixProviders.AddRange(fixers);
		// 	var refactoringProviders = CodeRefactoringProviderLoader.LoadCodeRefactoringProviders(assemblies, language);
		// 	_codeRefactoringProviders.AddRange(refactoringProviders);
		// }
	}

	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public async Task ReloadSolution(CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadSolution)}");
		_logger.LogInformation("RoslynAnalysis: Reloading solution");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(_workspace, nameof(_workspace));
		Guard.Against.Null(_msBuildProjectLoader, nameof(_msBuildProjectLoader));

		// It is important to note that a Workspace has no concept of MSBuild, nuget packages etc. It is just told about project references and "metadata" references, which are dlls. This is the what MSBuild does - it reads the csproj, and most importantly resolves nuget package references to dlls
		await _buildService.MsBuildAsync(_sharpIdeSolutionModel!.FilePath, BuildType.Restore, BuildStartedFlags.UserFacing, cancellationToken);
		var __ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.MSBuildProjectLoader.LoadSolutionInfoAsync");
		// This call is the expensive part - MSBuild is slow. There doesn't seem to be any incrementalism for solutions.
		// The best we could do to speed it up is do .LoadProjectInfoAsync for the single project, and somehow munge that into the existing solution
		var (newSolutionInfo, projectFileInfos) = await _msBuildProjectLoader.LoadSolutionInfoAsync(_sharpIdeSolutionModel!.FilePath, cancellationToken: cancellationToken);
		_projectFileInfoMap = projectFileInfos;
		__?.Dispose();

		var ___ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.Workspace.OnSolutionReloaded");
		// There doesn't appear to be any noticeable difference between ClearSolution + AddSolution vs OnSolutionReloaded
		//_workspace.OnSolutionReloaded(newSolutionInfo);
		_workspace.ClearSolution();
		_workspace.AddSolution(newSolutionInfo);
		___?.Dispose();
		_logger.LogInformation("RoslynAnalysis: Solution reloaded");
	}

	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public async Task ReloadProject(SharpIdeProjectModel projectModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadProject)}");
		_logger.LogInformation("RoslynAnalysis: Reloading project {ProjectPath}", projectModel.FilePath);
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(_workspace, nameof(_workspace));
		Guard.Against.Null(_msBuildProjectLoader, nameof(_msBuildProjectLoader));

		await _buildService.MsBuildAsync(_sharpIdeSolutionModel!.FilePath, BuildType.Restore, BuildStartedFlags.Internal, cancellationToken);
		var __ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(CustomMsBuildProjectLoader)}.{nameof(CustomMsBuildProjectLoader.LoadProjectInfosAsync)}");

		var thisProject = GetProjectForSharpIdeProjectModel(projectModel);

		// we can reliably rely on the Solution's graph of project inter-references, as a project has only been reloaded - no projects have been added or removed from the solution
		var dependentProjects = _workspace.CurrentSolution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(thisProject.Id);
		var projectPathsToReload = dependentProjects.Select(id => _workspace.CurrentSolution.GetProject(id)!.FilePath!).Append(thisProject.FilePath!).Distinct().ToList();
		//var projectMap = ProjectMap.Create(_workspace.CurrentSolution); // using a projectMap may speed up LoadProjectInfosAsync, TODO: test
		// This will get all projects necessary to build this group of projects, regardless of whether those projects are actually affected by the original project change
		// We can potentially optimise this, but given this is the expensive part, lets just proceed with reloading them all in the solution
		// We potentially lose performance because Workspace/Solution caches are dropped, but lets not prematurely optimise
		var (loadedProjectInfos, projectFileInfos) = await _msBuildProjectLoader.LoadProjectInfosAsync(projectPathsToReload, null, cancellationToken: cancellationToken);
		foreach (var (projectId, projectFileInfo) in projectFileInfos)
		{
			_projectFileInfoMap[projectId] = projectFileInfo;
		}
		__?.Dispose();

		var ___ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.Workspace.UpdateSolution");

		var oldProjectIdFilePathMap = _workspace.CurrentSolution.Projects.ToDictionary(keySelector: static p => (p.FilePath!, p.Name), elementSelector: static p => p.Id);

		var projectIdMap = loadedProjectInfos.ToDictionary(
			keySelector: static info => info.Id,
			elementSelector: info => oldProjectIdFilePathMap[(info.FilePath!, info.Name)]);

		// When we "reload" a project, we assume: no projects have been removed from the solution, and none added (TODO: Consider/handle a project gaining a project reference to a project outside of the solution)
		// Therefore, loadedProjectInfos ⊆ (is a subset of) _workspace.CurrentSolution.Projects
		// The ProjectIds will not match however, so we need to match on FilePath
		// Since the ProjectIds don't match, we also need to remap all ProjectReferences to the existing ProjectIds
		// same for documents
		var projectInfosToUpdateWith = loadedProjectInfos.Select(loadedProjectInfo =>
		{
			var existingProject = _workspace.CurrentSolution.Projects.Single(p => p.FilePath == loadedProjectInfo.FilePath && p.Name == loadedProjectInfo.Name);
			var projectInfo = loadedProjectInfo
				.WithId(existingProject.Id)
				.WithDocuments(MapDocuments(_workspace.CurrentSolution, existingProject.Id, loadedProjectInfo.Documents))
				.WithProjectReferences(loadedProjectInfo.ProjectReferences.Select(MapProjectReference))
				.WithAdditionalDocuments(MapDocuments(_workspace.CurrentSolution, existingProject.Id, loadedProjectInfo.AdditionalDocuments))
				.WithAnalyzerConfigDocuments(MapDocuments(_workspace.CurrentSolution, existingProject.Id, loadedProjectInfo.AnalyzerConfigDocuments));
			return projectInfo;
		}).ToList();

		var newSolution = _workspace.CurrentSolution;
		foreach (var projectInfo in projectInfosToUpdateWith)
		{
			newSolution = newSolution.WithProjectInfo(projectInfo);
		}
		// Doesn't raise a workspace change event, for now we don't care?
		_workspace.SetCurrentSolution(newSolution);

		// We should potentially use the below instead of SetCurrentSolution, as it is async, and potentially has better locking semantics
		// I think we will run into this imminently, when we handle multiple rapid project reloads
		// await _workspace.SetCurrentSolutionAsync(true,
		// 	transformation: oldSolution =>
		// 	{
		// 		// Move above code in here
		// 		return oldSolution;
		// 	},
		// 	changeKind: (oldSln, newSln) => (WorkspaceChangeKind.SolutionChanged, null, null),
		// 	null, null, cancellationToken
		// );

		_workspace.UpdateReferencesAfterAdd();

		___?.Dispose();
		_logger.LogInformation("RoslynAnalysis: Project reloaded");
		return;
		ProjectReference MapProjectReference(ProjectReference oldRef) => new ProjectReference(projectIdMap[oldRef.ProjectId], oldRef.Aliases, oldRef.EmbedInteropTypes);

		static ImmutableArray<DocumentInfo> MapDocuments(Solution oldSolution, ProjectId mappedProjectId, IReadOnlyList<DocumentInfo> documents)
			=> documents.Select(docInfo =>
			{
				var mappedDocumentId = oldSolution.GetDocumentIdsWithFilePath(docInfo.FilePath).Single(id => id.ProjectId == mappedProjectId);
				return docInfo.WithId(mappedDocumentId);
			}).ToImmutableArray();
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
			var sharpIdeProjectModel = _sharpIdeSolutionModel!.AllProjects.Single(p => p.FilePath == project.FilePath);
			await ReloadProject(sharpIdeProjectModel, cancellationToken);
		}

		return true;
	}

	public async Task UpdateSolutionDiagnostics(CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(UpdateSolutionDiagnostics)}");
		_logger.LogInformation("RoslynAnalysis: Updating solution diagnostics");
		var timer = Stopwatch.StartNew();
		await _solutionLoadedTcs.Task;
		// Performance improvements of ~15% have been observed with a large solution (100+ projects) by parallelizing this with Task.WhenAll, however it seems much heavier (14700K crashes sometimes 😅) so re-evaluate later
		foreach (var project in _sharpIdeSolutionModel!.AllProjects)
		{
			await UpdateProjectDiagnostics(project, cancellationToken);
		}
		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Solution diagnostics updated in {ElapsedMilliseconds}ms", timer.ElapsedMilliseconds);
	}

	public async Task UpdateProjectDiagnostics(SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		var projectDiagnostics = await GetProjectDiagnostics(project, cancellationToken);
		// TODO: only add and remove diffs
		project.Diagnostics.RemoveRange(project.Diagnostics);
		project.Diagnostics.AddRange(projectDiagnostics);
	}

	public async Task UpdateProjectDiagnosticsForFile(SharpIdeFile sharpIdeFile, CancellationToken cancellationToken = default)
	{
		var project = ((IChildSharpIdeNode) sharpIdeFile).GetNearestProjectNode();
		Guard.Against.Null(project);
		await UpdateProjectDiagnostics(project, cancellationToken);
	}

	public async Task<ImmutableArray<SharpIdeDiagnostic>> GetProjectDiagnostics(SharpIdeProjectModel projectModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetProjectDiagnostics)}");
		await _solutionLoadedTcs.Task;
		var project = GetProjectForSharpIdeProjectModel(projectModel);
		var compilation = await project.GetCompilationAsync(cancellationToken);
		Guard.Against.Null(compilation, nameof(compilation));

		var allDiagnostics = compilation.GetDiagnostics(cancellationToken);
		var diagnostics = allDiagnostics
			.Where(d => d.Severity is not DiagnosticSeverity.Hidden)
			.Select(d =>
			{
				var mappedFileLinePositionSpan = d.Location.SourceTree!.GetMappedLineSpan(d.Location.SourceSpan);
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return diagnostics;
	}

	public async Task<ImmutableArray<SharpIdeDiagnostic>> GetProjectDiagnosticsForFile(SharpIdeFile sharpIdeFile, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetProjectDiagnosticsForFile)}");
		await _solutionLoadedTcs.Task;
		if (sharpIdeFile.IsRoslynWorkspaceFile is false) return [];
		var project = GetProjectForSharpIdeFile(sharpIdeFile);
		var compilation = await project.GetCompilationAsync(cancellationToken);
		Guard.Against.Null(compilation, nameof(compilation));

		var document = await GetDocumentForSharpIdeFile(sharpIdeFile, cancellationToken);

		var syntaxTree = compilation.SyntaxTrees.Single(s => s.FilePath == document.FilePath);
		var diagnostics = compilation.GetDiagnostics(cancellationToken)
			.Where(d => d.Severity is not DiagnosticSeverity.Hidden && d.Location.SourceTree == syntaxTree)
			.Select(d =>
			{
				var mappedFileLinePositionSpan = d.Location.SourceTree!.GetMappedLineSpan(d.Location.SourceSpan);
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return diagnostics;
	}

	public async Task<ImmutableArray<SharpIdeDiagnostic>> GetDocumentDiagnostics(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return [];
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentDiagnostics)}");
		await _solutionLoadedTcs.Task;

		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document, nameof(document));

		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
		diagnostics = diagnostics.Where(d => d.Severity is not DiagnosticSeverity.Hidden).ToImmutableArray();
		var result = diagnostics
			.Select(d =>
			{
				var mappedFileLinePositionSpan = semanticModel.SyntaxTree.GetMappedLineSpan(d.Location.SourceSpan);
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return result;
	}

	public async Task<ImmutableArray<SharpIdeDiagnostic>> GetDocumentAnalyzerDiagnostics(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return [];
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentAnalyzerDiagnostics)}");
		await _solutionLoadedTcs.Task;

		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document, nameof(document));

		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		var projectAnalyzers = document.Project.AnalyzerReferences
			.OfType<IsolatedAnalyzerFileReference>()
			.SelectMany(r => r.GetAnalyzers(document.Project.Language))
			.ToImmutableArray();

		var compilationWithAnalyzers = semanticModel.Compilation.WithAnalyzers(projectAnalyzers);

		var analysisResult = await compilationWithAnalyzers.GetAnalysisResultAsync(semanticModel, null, cancellationToken);
		var diagnostics = analysisResult.GetAllDiagnostics();
		diagnostics = diagnostics.Where(d => d.Severity is not DiagnosticSeverity.Hidden).ToImmutableArray();
		var result = diagnostics
			.Select(d =>
			{
				var mappedFileLinePositionSpan = semanticModel.SyntaxTree.GetMappedLineSpan(d.Location.SourceSpan);
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return result;
	}

	private static async Task<Document> GetDocumentForSharpIdeFile(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		var project = GetProjectForSharpIdeFile(fileModel);
		var document = fileModel.IsCsharpFile ? project.Documents.SingleOrDefault(s => s.FilePath == fileModel.Path)
				: await GetRazorSourceGeneratedDocumentInProjectForSharpIdeFile(project, fileModel, cancellationToken);
		Guard.Against.Null(document, nameof(document));
		return document;
	}

	private static async Task<SourceGeneratedDocument> GetRazorSourceGeneratedDocumentInProjectForSharpIdeFile(Project project, SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		var razorDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(razorDocument);

		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		return generatedDocument;
	}

	public record SharpIdeRazorMappedClassifiedSpan(SharpIdeRazorSourceSpan SourceSpanInRazor, string CsharpClassificationType);
	public async Task<ImmutableArray<SharpIdeRazorClassifiedSpan>> GetRazorDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetRazorDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		var timer = Stopwatch.StartNew();
		var project = GetProjectForSharpIdeFile(fileModel);
		if (fileModel.IsRazorFile is false)
		{
			return [];
			//throw new InvalidOperationException("File is not a .razor file");
		}
		var razorDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(razorDocument);

		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		var razorCSharpDocument = razorCodeDocument.GetRequiredCSharpDocument();
		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);
		//var razorCsharpText = razorCSharpDocument.Text.ToString();
		//var razorSyntaxRoot = razorCodeDocument.GetRequiredSyntaxRoot();

		var razorText = await razorDocument.GetTextAsync(cancellationToken);

		List<string> relevantTypes = ["razorDirective", "razorTransition", "markupTextLiteral", "markupTagDelimiter", "markupElement", "razorComponentElement", "razorComponentAttribute", "razorComment", "razorCommentTransition", "razorCommentStar", "markupOperator", "markupAttributeQuote"];
		var ranges = new List<SemanticRange>();
		CustomSemanticTokensVisitor.AddSemanticRanges(ranges, razorCodeDocument, generatedDocSyntaxRoot!.FullSpan, _semanticTokensLegendService!, false);

		//var allTypes = ranges.Select(s => _semanticTokensLegendService!.TokenTypes.All[s.Kind]).Distinct().ToList();
		var semanticRangeRazorSpans = ranges
			.Where(s => relevantTypes.Contains(_semanticTokensLegendService!.TokenTypes.All[s.Kind]))
			.Select(s =>
			{
				var linePositionSpan = s.AsLinePositionSpan();
				var textSpan = razorText.GetTextSpan(linePositionSpan);
				var sourceSpan = new SourceSpan(
					fileModel.Path,
					textSpan.Start,
					linePositionSpan.Start.Line,
					linePositionSpan.Start.Character,
					textSpan.Length,
					1,
					linePositionSpan.End.Character
				);
				return new SharpIdeRazorClassifiedSpan(sourceSpan.ToSharpIdeSourceSpan(), SharpIdeRazorSpanKind.Markup, null, _semanticTokensLegendService!.TokenTypes.All[s.Kind]);
			}).ToList();

		// var debugMappedBackTranslatedSemanticRanges = relevantRanges.Select(s =>
		// {
		// 	var textSpan = razorText.GetTextSpan(s.Range.AsLinePositionSpan());
		// 	var text = razorText.GetSubTextString(textSpan);
		// 	return new { text, s };
		// }).ToList();
		// var semanticRangesAsRazorClassifiedSpans = ranges
		// 	.Select(s =>
		// 	{
		// 		var sourceSpan = new SharpIdeRazorSourceSpan(null, s.)
		// 		var span = new SharpIdeRazorClassifiedSpan();
		// 		return span;
		// 	}).ToList();
		//var test = _semanticTokensLegendService.TokenTypes.All;
		var sourceMappings = razorCSharpDocument.SourceMappings.Select(s => s.ToSharpIdeSourceMapping()).ToImmutableArray();
		List<SharpIdeRazorClassifiedSpan> sharpIdeRazorSpans = [];

		var classifiedSpans = await Classifier.GetClassifiedSpansAsync(generatedDocument, generatedDocSyntaxRoot!.FullSpan, cancellationToken);
		var roslynMappedSpans = classifiedSpans.Select(s =>
		{
			var genSpan = s.TextSpan;
			var mapping = sourceMappings.SingleOrDefault(m => m.GeneratedSpan.AsTextSpan().IntersectsWith(genSpan));
			if (mapping != null)
			{
				// Translate generated span back to Razor span
				var offset = genSpan.Start - mapping.GeneratedSpan.AbsoluteIndex;
				var mappedStart = mapping.OriginalSpan.AbsoluteIndex + offset;
				var mappedSpan = new TextSpan(mappedStart, genSpan.Length);
				var sharpIdeSpan = new SharpIdeRazorSourceSpan(
					mapping.OriginalSpan.FilePath,
					mappedSpan.Start,
					razorText.Lines.GetLineFromPosition(mappedSpan.Start).LineNumber,
					mappedSpan.Start - razorText.Lines.GetLineFromPosition(mappedSpan.Start).Start,
					mappedSpan.Length,
					1,
					mappedSpan.Start - razorText.Lines.GetLineFromPosition(mappedSpan.Start).Start + mappedSpan.Length
				);

				return new SharpIdeRazorMappedClassifiedSpan(
					sharpIdeSpan,
					s.ClassificationType
				);
			}

			return null;
		}).Where(s => s is not null).ToList();

		sharpIdeRazorSpans = [
			..sharpIdeRazorSpans.Where(s => s.Kind is not SharpIdeRazorSpanKind.Code),
			..roslynMappedSpans.Select(s => new SharpIdeRazorClassifiedSpan(s!.SourceSpanInRazor, SharpIdeRazorSpanKind.Code, s.CsharpClassificationType)),
			..semanticRangeRazorSpans
		];
		var result = sharpIdeRazorSpans.OrderBy(s => s.Span.AbsoluteIndex).ToImmutableArray();
		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Razor syntax highlighting for {FileName} took {ElapsedMilliseconds}ms", fileModel.Name, timer.ElapsedMilliseconds);
		return result;
	}

	// This is expensive for files that have just been updated, making it suboptimal for real-time highlighting
	public async Task<ImmutableArray<SharpIdeClassifiedSpan>> GetDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		var project = GetProjectForSharpIdeFile(fileModel);
		if (fileModel.IsCsharpFile is false)
		{
			//throw new InvalidOperationException("File is not a .cs");
			return [];
		}

		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));

		var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
		var root = await syntaxTree!.GetRootAsync(cancellationToken);

		var classifiedSpans = await Classifier.GetClassifiedSpansAsync(document, root.FullSpan, cancellationToken);
		var result = classifiedSpans.Select(s => new SharpIdeClassifiedSpan(syntaxTree.GetMappedLineSpan(s.TextSpan).Span, s)).ToImmutableArray();
		return result;
	}

	// We store the document here, so that we have the correct version of the document when we compute completions
	// This may not be the best way to do this, but it seems to work okay. It may only be a problem because I continue to update the doc in the workspace as the user continues typing, filtering the completion
	// I could possibly pause updating the document while the completion list is open, but that seems more complex - handling accepted vs cancelled completions etc
	public record IdeCompletionListResult(Document Document, CompletionList CompletionList, LinePosition LinePosition);
	public async Task<IdeCompletionListResult> GetCodeCompletionsForDocumentAtPosition(SharpIdeFile fileModel, LinePosition linePosition, CompletionTrigger completionTrigger)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeCompletionsForDocumentAtPosition)}");
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(fileModel);
		Guard.Against.Null(document, nameof(document));
		var (completions, triggerLinePosition) = await GetCompletionsAsync(document, linePosition, completionTrigger).ConfigureAwait(false);
		return new IdeCompletionListResult(document, completions, triggerLinePosition);
	}

	// TODO: Pass in LinePositionSpan for refactorings that span multiple characters, e.g. extract method
	public async Task<ImmutableArray<CodeAction>> GetCodeActionsForDocumentAtPosition(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeActionsForDocumentAtPosition)}");
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document, nameof(document));
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		// We don't need analyzer diagnostics, as ICodeFixService does not take diagnostics to find fixes for, it takes a document and span
		var diagnostics = semanticModel.Compilation.GetDiagnostics(cancellationToken);

		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.Lines.GetPosition(linePosition);
		var diagnosticsAtPosition = diagnostics
			.Where(d => d.Location.IsInSource && d.Location.SourceSpan.Contains(position))
			.ToImmutableArray();

		var arrayBuilder = ImmutableArray.CreateBuilder<CodeAction>();
		foreach (var diagnostic in diagnosticsAtPosition)
		{
			var actions = await GetCodeFixesAsync(document, diagnostic, cancellationToken);
			arrayBuilder.AddRange(actions);
		}

		var textSpan = new TextSpan(position, 0);
		var codeActionsFromProjectAnalyzers = await GetCodeFixesFromProjectAnalyzersAsync(document, textSpan, cancellationToken);
		arrayBuilder.AddRange(codeActionsFromProjectAnalyzers);

		var linePositionSpan = new LinePositionSpan(linePosition, new LinePosition(linePosition.Line, linePosition.Character + 1));
		var selectedSpan = sourceText.Lines.GetTextSpan(linePositionSpan);

		var codeRefactorings = await GetCodeRefactoringsAsync(document, selectedSpan, cancellationToken);
		arrayBuilder.AddRange(codeRefactorings);

		return arrayBuilder.ToImmutable();
	}

	// Fixes from the MefHostServices.DefaultAssemblies
	private static async Task<ImmutableArray<CodeAction>> GetCodeFixesAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken = default)
	{
		var codeActions = new List<CodeAction>();
		var context = new CodeFixContext(
			document,
			diagnostic,
			(action, _) => codeActions.Add(action), // callback collects fixes
			cancellationToken
		);

		var relevantProviders = _codeFixProviders.Where(provider => provider.FixableDiagnosticIds.Contains(diagnostic.Id));

		foreach (var provider in relevantProviders)
		{
			await provider.RegisterCodeFixesAsync(context);
		}

		return codeActions.ToImmutableArray();
	}

	private static async Task<ImmutableArray<CodeAction>> GetCodeFixesFromProjectAnalyzersAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
	{
		// We could get the CodeFixProviders from the project's IsolatedAnalyzerFileReferences. For now, ICodeFixService handles caching them for me
		// I also do not know why the _codeFixService does not return fixes for MefHostServices.DefaultAssemblies - I have verified that there are Lazy<CodeFixProvider, CodeChangeProviderMetadata>>'s provided by the composition for them
		var fixCollections = await _codeFixService!.GetFixesAsync(
			document,
			span,
			cancellationToken);

		var codeActions = fixCollections
			.SelectMany(collection => collection.Fixes)
			.Select(fix => fix.Action)
			.Where(s => s.NestedActions.Length is 0) // Currently, nested actions are not supported
			.ToImmutableArray();

		return codeActions;
	}

	private static async Task<ImmutableArray<CodeAction>> GetCodeRefactoringsAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
	{
		var refactorings = await _codeRefactoringService!.GetRefactoringsAsync(
			document,
			span,
			cancellationToken);

		var codeActions = refactorings
			.SelectMany(collection => collection.CodeActions)
			.Where(s => s.action.NestedActions.Length is 0) // Currently, nested actions are not supported
			.Select(s => s.action)
			.ToImmutableArray();

		return codeActions;
	}

	[Obsolete] // ICodeRefactoringService seems to return refactorings from DefaultAssemblies! Unlike ICodeFixService. Leaving for reference
	private static async Task<ImmutableArray<CodeAction>> GetCodeRefactoringsFromDefaultAssembliesAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
	{
		var codeActions = new List<CodeAction>();
		var refactorContext = new CodeRefactoringContext(
			document,
			span,
			action => codeActions.Add(action),
			cancellationToken
		);

		foreach (var provider in _codeRefactoringProviders)
		{
			await provider.ComputeRefactoringsAsync(refactorContext).ConfigureAwait(false);
		}

		return codeActions.ToImmutableArray();
	}

	private static async Task<(CompletionList completions, LinePosition triggerLinePosition)> GetCompletionsAsync(Document document, LinePosition linePosition, CompletionTrigger completionTrigger, CancellationToken cancellationToken = default)
	{
		var completionService = CompletionService.GetService(document);
		if (completionService is null) throw new InvalidOperationException("Completion service is not available for the document.");

		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.Lines.GetPosition(linePosition);
		var completions = await completionService.GetCompletionsAsync(document, position, completionTrigger, cancellationToken: cancellationToken);
		var triggerLinePosition = sourceText.GetLinePosition(completions.Span.Start);
		return (completions, triggerLinePosition);
	}

	// Currently unused
	public async Task<bool> ShouldTriggerCompletionAsync(SharpIdeFile file, string documentText, LinePosition linePosition, CompletionTrigger completionTrigger, CancellationToken cancellationToken = default)
	{
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(file, cancellationToken);
		var completionService = CompletionService.GetService(document);
		if (completionService is null) throw new InvalidOperationException("Completion service is not available for the document.");

		//var sourceText = await document.GetTextAsync(cancellationToken);
		var sourceText = SourceText.From(documentText, Encoding.UTF8);
		var position = sourceText.Lines.GetPosition(linePosition);
		var shouldTrigger = completionService.ShouldTriggerCompletion(document.Project, document.Project.Services, sourceText, position, completionTrigger, CompletionOptions.Default, document.Project.Solution.Options ?? OptionSet.Empty);
		return shouldTrigger;
	}

	public static ImmutableArray<SharpIdeCompletionItem> FilterCompletions(SharpIdeFile file, string documentText, LinePosition linePosition, CompletionList completionList, CompletionTrigger completionTrigger, CompletionFilterReason filterReason)
	{
		var sourceText = SourceText.From(documentText, Encoding.UTF8);
		var position = sourceText.Lines.GetPosition(linePosition);

		var filterSpanLength = position - completionList.Span.Start;
		// user has backspaced past the trigger span
		if (filterSpanLength < 0) return [];
		var filterSpan = new TextSpan(completionList.Span.Start, length: filterSpanLength);

		var filteredCompletionItems = FilterCompletionList(completionList, filterSpan, completionTrigger, filterReason, sourceText);
		return filteredCompletionItems;
	}

	private static ImmutableArray<SharpIdeCompletionItem> FilterCompletionList(CompletionList completionList, TextSpan filterSpan, CompletionTrigger completionTrigger, CompletionFilterReason filterReason, SourceText sourceText)
    {
        var filterText = sourceText.GetSubText(filterSpan).ToString();
		Console.WriteLine($"Filter text: '{filterText}'");

        // Use pattern matching to determine which items are most relevant out of the calculated items.
        using var _ = ArrayBuilder<MatchResult>.GetInstance(out var matchResultsBuilder);
        var index = 0;
        using var helper = new PatternMatchHelper(filterText);
        foreach (var item in completionList.ItemsList)
        {
            if (helper.TryCreateMatchResult(
                item,
                completionTrigger.Kind,
                filterReason,
                recentItemIndex: -1,
                includeMatchSpans: true,
                index,
                out var matchResult))
            {
                matchResultsBuilder.Add(matchResult);
                index++;
            }
        }

        // Next, we sort the list based on the pattern matching result.
        matchResultsBuilder.Sort(MatchResult.SortingComparer);

        var filteredList = matchResultsBuilder.SelectAsArray(matchResult =>
	        new SharpIdeCompletionItem(matchResult.CompletionItem, matchResult.PatternMatch?.MatchedSpans));

        return filteredList;
    }

	public async Task<(string updatedText, SharpIdeFileLinePosition sharpIdeFileLinePosition)> GetCompletionApplyChanges(SharpIdeFile file, CompletionItem completionItem, Document document, CancellationToken cancellationToken = default)
	{
		//var documentId = _workspace!.CurrentSolution.GetDocumentIdsWithFilePath(file.Path).Single();
		//var document = SolutionExtensions.GetRequiredDocument(_workspace.CurrentSolution, documentId);
		var completionService = CompletionService.GetService(document) ?? throw new InvalidOperationException("Completion service is not available for the document.");
		var completionChange = await completionService.GetChangeAsync(document, completionItem, commitCharacter: '.', cancellationToken: cancellationToken);
		var sourceText = await document.GetTextAsync(cancellationToken);
		var newText = sourceText.WithChanges(completionChange.TextChange);
		var newCaretPosition = completionChange.NewPosition ?? NewCaretPosition();
		var linePosition = newText.Lines.GetLinePosition(newCaretPosition);
		var sharpIdeFileLinePosition = new SharpIdeFileLinePosition
		{
			Line = linePosition.Line,
			Column = linePosition.Character
		};

		return (newText.ToString(), sharpIdeFileLinePosition);

		int NewCaretPosition()
		{
			var caretPosition = completionChange.TextChange.Span.Start + completionChange.TextChange.NewText!.Length;
			// if change ends with (), place caret between the parentheses
			if (completionChange.TextChange.NewText!.EndsWith("()"))
			{
				caretPosition -= 1;
			}
			return caretPosition;
		}
	}

	/// Returns the list of files that would be modified by applying the code action. Does not apply the changes to the workspace sln
	public async Task<List<(SharpIdeFile File, string UpdatedText)>> GetCodeActionApplyChanges(CodeAction codeAction, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeActionApplyChanges)}");
		await _solutionLoadedTcs.Task;
		// TODO: Handle codeAction.NestedActions
		var operations = await codeAction.GetOperationsAsync(cancellationToken);
		var originalSolution = _workspace!.CurrentSolution;
		var updatedSolution = originalSolution;
		foreach (var operation in operations)
		{
			if (operation is ApplyChangesOperation applyChangesOperation)
			{
				var changes = applyChangesOperation.ChangedSolution.GetChanges(updatedSolution);
				// Linked files are e.g. files with the same path, but different projects, ie different TFMs
				updatedSolution = await applyChangesOperation.ChangedSolution.WithMergedLinkedFileChangesAsync(updatedSolution, changes, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				throw new NotSupportedException($"Unsupported operation type: {operation.GetType().Name}");
			}
		}

		var changedFilesWithText = await GetNaiveSolutionChanges(originalSolution, updatedSolution, cancellationToken);
		return changedFilesWithText;
	}

	private async Task<List<(SharpIdeFile File, string UpdatedText)>> GetNaiveSolutionChanges(Solution oldSolution, Solution newSolution, CancellationToken cancellationToken = default)
	{
		var allChanges = newSolution.GetChanges(oldSolution);
		// TODO: Handle added and removed documents
		var changedDocIds = allChanges
			.GetExplicitlyChangedSourceGeneratedDocuments().Union(allChanges
				.GetProjectChanges()
				.SelectMany(s => s.GetChangedDocuments().Union(s.GetChangedAdditionalDocuments()))).ToHashSet();

		var changedFilesWithText = await changedDocIds
			.DistinctBy(s => s.Id) // probably not necessary
			.Select(id => newSolution.GetDocument(id))
			//.Select(id => updatedSolution.GetDocument(id) ?? await _workspace.CurrentSolution.GetSourceGeneratedDocumentAsync(id, cancellationToken))
			.Where(d => d is not null)
			.OfType<Document>() // ensures non-null
			.ToAsyncEnumerable()
			.Select(async (Document doc, CancellationToken ct) =>
			{
				var text = await doc.GetTextAsync(ct);
				var sharpFile = _sharpIdeSolutionModel!.AllFiles[doc.FilePath!];
				return (sharpFile, text.ToString());
			})
			.ToListAsync(cancellationToken);
		return changedFilesWithText;
	}

	public record IdeReferenceLocationResult(ReferenceLocation ReferenceLocation, SharpIdeFile? File, ISymbol? EnclosingSymbol);
	public async Task<IdeReferenceLocationResult?> GetIdeReferenceLocationResult(ReferenceLocation referenceLocation)
	{
		var semanticModel = await referenceLocation.Document.GetSemanticModelAsync();
		if (semanticModel is null) return null;
		var enclosingSymbol = ReferenceLocationExtensions.GetEnclosingMethodOrPropertyOrField(semanticModel, referenceLocation);
		var lineSpan = referenceLocation.Location.GetMappedLineSpan();
		var file = _sharpIdeSolutionModel!.AllFiles[lineSpan.Path];
		var result = new IdeReferenceLocationResult(referenceLocation, file, enclosingSymbol);
		return result;
	}

	public async Task<ImmutableArray<IdeReferenceLocationResult>> GetIdeReferenceLocationResults(ImmutableArray<ReferenceLocation> referenceLocations)
	{
		var results = new List<IdeReferenceLocationResult>();
		foreach (var referenceLocation in referenceLocations)
		{
			var result = await GetIdeReferenceLocationResult(referenceLocation);
			if (result is not null)
			{
				results.Add(result);
			}
		}
		return results.ToImmutableArray();
	}

	/// Returns the list of files that would be modified by applying the rename. Does not apply the changes to the workspace sln
	public async Task<List<(SharpIdeFile File, string UpdatedText)>> GetRenameApplyChanges(ISymbol symbol, string newName, CancellationToken cancellationToken = default)
	{
		var symbolRenameOptions = new SymbolRenameOptions
		{
			RenameOverloads = true,
			RenameInStrings = false,
			RenameInComments = false,
			RenameFile = false
		};
		var currentSolution = _workspace!.CurrentSolution;
		var newSolution = await Renamer.RenameSymbolAsync(currentSolution, symbol, symbolRenameOptions, newName, cancellationToken);
		var changedFilesWithText = await GetNaiveSolutionChanges(currentSolution, newSolution, cancellationToken);
		return changedFilesWithText;
	}

	public async Task<ImmutableArray<ReferencedSymbol>> FindAllSymbolReferences(ISymbol symbol, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(FindAllSymbolReferences)}");
		await _solutionLoadedTcs.Task;

		var solution = _workspace!.CurrentSolution;
		var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
		return references.ToImmutableArray();
	}

	public async Task<(ISymbol?, LinePositionSpan?, TokenSemanticInfo?)> LookupSymbolSemanticInfo(SharpIdeFile fileModel, LinePosition linePosition)
	{
		await _solutionLoadedTcs.Task;
		var (symbol, linePositionSpan, semanticInfo) = fileModel switch
		{
			{ IsRazorFile: true } => await LookupSymbolSemanticInfoInRazor(fileModel, linePosition),
			{ IsCsharpFile: true } => await LookupSymbolSemanticInfoInCs(fileModel, linePosition),
			_ => (null, null, null)
		};
		return (symbol, linePositionSpan, semanticInfo);
	}

	public async Task<(ISymbol?, LinePositionSpan?)> LookupSymbol(SharpIdeFile fileModel, LinePosition linePosition)
	{
		await _solutionLoadedTcs.Task;
		var (symbol, linePositionSpan) = fileModel switch
		{
			{ IsRazorFile: true } => await LookupSymbolInRazor(fileModel, linePosition),
			{ IsCsharpFile: true } => await LookupSymbolInCs(fileModel, linePosition),
			_ => (null, null)
		};
		return (symbol, linePositionSpan);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan)> LookupSymbolInRazor(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var project = GetProjectForSharpIdeFile(fileModel);

		var additionalDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(additionalDocument);

		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		var razorCSharpDocument = razorCodeDocument.GetRequiredCSharpDocument();
		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);
		var razorAbsoluteIndex = razorText.Lines.GetPosition(linePosition);
		var mappedPosition = MapRazorLinePositionToGeneratedCSharpAbsolutePosition(razorCSharpDocument, razorAbsoluteIndex);
		if (mappedPosition is null) return (null, null);

		var semanticModelAsync = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModelAsync!, generatedDocSyntaxRoot!, mappedPosition!.Value);
		if (symbol is null || linePositionSpan is null) return (null, null);
		Guard.Against.Null(linePositionSpan, nameof(linePositionSpan));
		if (_documentMappingService!.TryMapToRazorDocumentRange(razorCSharpDocument, linePositionSpan.Value, MappingBehavior.Strict, out var mappedRazorLinePositionSpan) is false)
		{
			throw new InvalidOperationException("Failed to map C# line position span back to Razor.");
		}
		return (symbol, mappedRazorLinePositionSpan);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan)> LookupSymbolInCs(SharpIdeFile fileModel, LinePosition linePosition)
	{
		var project = GetProjectForSharpIdeFile(fileModel);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));
		var sourceText = await document.GetTextAsync();
		var position = sourceText.GetPosition(linePosition);
		var semanticModel = await document.GetSemanticModelAsync();
		Guard.Against.Null(semanticModel, nameof(semanticModel));
		var syntaxRoot = await document.GetSyntaxRootAsync();
		return GetSymbolAtPosition(semanticModel, syntaxRoot!, position);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan, TokenSemanticInfo? semanticInfo)> LookupSymbolSemanticInfoInCs(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var project = GetProjectForSharpIdeFile(fileModel);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));
		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.GetPosition(linePosition);
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));
		var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
		var semanticInfo = await SymbolFinder.GetSemanticInfoAtPositionAsync(semanticModel, position, document.Project.Solution.Services, cancellationToken).ConfigureAwait(false);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModel, syntaxRoot!, position);
		return (symbol, linePositionSpan, semanticInfo);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan, TokenSemanticInfo? semanticInfo)> LookupSymbolSemanticInfoInRazor(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var project = GetProjectForSharpIdeFile(fileModel);

		var additionalDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(additionalDocument);

		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		var razorCSharpDocument = razorCodeDocument.GetRequiredCSharpDocument();
		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);
		var razorAbsoluteIndex = razorText.Lines.GetPosition(linePosition);
		var mappedPosition = MapRazorLinePositionToGeneratedCSharpAbsolutePosition(razorCSharpDocument, razorAbsoluteIndex);

		var semanticModel = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModel!, generatedDocSyntaxRoot!, mappedPosition!.Value);

		var semanticInfo = await SymbolFinder.GetSemanticInfoAtPositionAsync(semanticModel!, mappedPosition.Value, generatedDocument.Project.Solution.Services, cancellationToken).ConfigureAwait(false);
		return (symbol, linePositionSpan, semanticInfo);
	}

	private (ISymbol? symbol, LinePositionSpan? linePositionSpan) GetSymbolAtPosition(SemanticModel semanticModel, SyntaxNode root, int position)
	{
		var node = root.FindToken(position).Parent!;
		var symbol = semanticModel.GetSymbolInfo(node).Symbol ?? semanticModel.GetDeclaredSymbol(node);
		if (symbol is null)
		{
			_logger.LogInformation("No symbol found at position {Position}", position);
			return (null, null);
		}

		var span = node switch
		{
			MethodDeclarationSyntax methodDecl => methodDecl.Identifier.Span,
			ClassDeclarationSyntax classDecl => classDecl.Identifier.Span,
			StructDeclarationSyntax structDecl => structDecl.Identifier.Span,
			InterfaceDeclarationSyntax interfaceDecl => interfaceDecl.Identifier.Span,
			EnumDeclarationSyntax enumDecl => enumDecl.Identifier.Span,
			DelegateDeclarationSyntax delegateDecl => delegateDecl.Identifier.Span,
			ConstructorDeclarationSyntax constructorDecl => constructorDecl.Identifier.Span,
			DestructorDeclarationSyntax destructorDecl => destructorDecl.Identifier.Span,
			PropertyDeclarationSyntax propDecl => propDecl.Identifier.Span,
			EventDeclarationSyntax eventDecl => eventDecl.Identifier.Span,
			VariableDeclaratorSyntax variableDecl => variableDecl.Identifier.Span,
			AccessorDeclarationSyntax accessorDecl => accessorDecl.Keyword.Span,
			IndexerDeclarationSyntax indexerDecl => indexerDecl.ThisKeyword.Span,

			GenericNameSyntax genericDecl => genericDecl.Identifier.Span,
			IdentifierNameSyntax identifierDecl => identifierDecl.Identifier.Span,
			_ => node.Span
		};

		var linePositionSpan = root.SyntaxTree.GetLineSpan(span).Span;
		_logger.LogInformation("Symbol found: {SymbolName} ({SymbolKind}) - {SymbolDisplayString}", symbol.Name, symbol.Kind, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
		return (symbol, linePositionSpan);
	}

	private static int? MapRazorLinePositionToGeneratedCSharpAbsolutePosition(RazorCSharpDocument razorCSharpDocument, int razorAbsoluteIndex)
	{
		if (_documentMappingService!.TryMapToCSharpDocumentPosition(razorCSharpDocument, razorAbsoluteIndex, out var csharpPosition, out var csharpIndex))
		{
			return csharpIndex;
		}
		return null;
	}

	// Returns true if the document was found and updated, otherwise false
	public async Task<bool> UpdateDocument(SharpIdeFile fileModel, string newContent)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(UpdateDocument)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(fileModel, nameof(fileModel));
		Guard.Against.NullOrEmpty(newContent, nameof(newContent));

		var documentIdsWithFilePath = _workspace!.CurrentSolution.GetDocumentIdsWithFilePath(fileModel.Path);
		var documentId = documentIdsWithFilePath.FirstOrDefault(); // Linked files should take care of the rest of the documents with the same path
		if (documentId is null)
		{
			_logger.LogTrace("UpdateDocument failed: Document '{DocumentPath}' not found in workspace", fileModel.Path);
			return false;
		}

		var newText = SourceText.From(newContent, Encoding.UTF8);

		// We don't blow up if the document is not in the workspace - this would happen e.g. for files that are excluded.
		// Roslyn implementations seem to handle this with a Misc Files workspace. TODO: Investigate
		var currentSolution = _workspace!.CurrentSolution;
		if (currentSolution.ContainsDocument(documentId))
		{
			_workspace.OnDocumentTextChanged(documentId, newText, PreservationMode.PreserveIdentity, requireDocumentPresent: false);
		}
		else if (currentSolution.ContainsAdditionalDocument(documentId))
		{
			_workspace.OnAdditionalDocumentTextChanged(documentId, newText, PreservationMode.PreserveIdentity);
		}
		else if (currentSolution.ContainsAnalyzerConfigDocument(documentId))
		{
			_workspace.OnAnalyzerConfigDocumentTextChanged(documentId, newText, PreservationMode.PreserveIdentity);
		}
		else
		{
			return false;
		}
		return true;
	}

	public async Task<bool> AddDocument(SharpIdeFile fileModel, string content)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(AddDocument)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(fileModel, nameof(fileModel));
		Guard.Against.Null(content, nameof(content));

		var sharpIdeProject = GetSharpIdeProjectForSharpIdeFile(fileModel);
		var probableProject = GetProjectForSharpIdeProjectModel(sharpIdeProject);
		// This file probably belongs to this project, but we need to check its path against the globs for the project to make sure
		var projectFileInfo = _projectFileInfoMap.GetValueOrDefault(probableProject.Id);
		Guard.Against.Null(projectFileInfo);

		var generatedFilesOutputDirectory = projectFileInfo.GeneratedFilesOutputDirectory;
		if (generatedFilesOutputDirectory is not null && fileModel.Path.StartsWith(generatedFilesOutputDirectory, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		// This may not be perfect, as None Include="" seems to be returned here as one of the globs as Include, with no distinction of Compile vs None etc
		// TODO: Investigate getting the glob type (Compile, None, etc)
		var matchers = projectFileInfo.FileGlobs.Select(glob =>
		{
			var matcher = new Matcher();
			matcher.AddIncludePatterns(glob.Includes);
			matcher.AddExcludePatterns(glob.Excludes);
			matcher.AddExcludePatterns(glob.Removes);
			return matcher;
		});

		var belongsToProject = false;
		// Check if the file path matches any of the globs in the project file.
		foreach (var matcher in matchers)
		{
			// CPS re-creates the msbuild globs from the includes/excludes/removes and the project XML directory and
			// ignores the MSBuildGlob.FixedDirectoryPart.  We'll do the same here and match using the project directory as the relative path.
			// See https://devdiv.visualstudio.com/DevDiv/_git/CPS?path=/src/Microsoft.VisualStudio.ProjectSystem/Build/MsBuildGlobFactory.cs
			var relativeDirectory = sharpIdeProject.DirectoryPath;

			var matches = matcher.Match(relativeDirectory, fileModel.Path);
			if (matches.HasMatches)
			{
				belongsToProject = true;
				break;
			}
		}

		if (belongsToProject is false)
		{
			return false;
		}

		var existingDocumentIdsWithFilePath = _workspace!.CurrentSolution.GetDocumentIdsWithFilePath(fileModel.Path);
		if (!existingDocumentIdsWithFilePath.IsEmpty)
		{
			throw new InvalidOperationException($"AddDocument failed: Document '{fileModel.Path}' already exists in workspace");
		}

		var sourceText = SourceText.From(content, Encoding.UTF8);
		var documentId = DocumentId.CreateNewId(probableProject.Id);

		_workspace.SetCurrentSolution(oldSolution =>
		{
			var newSolution = fileModel switch
			{
				{ IsCsharpFile: true } => _workspace.CurrentSolution.AddDocument(documentId, fileModel.Name, sourceText, filePath: fileModel.Path),
				_ => _workspace.CurrentSolution.AddAdditionalDocument(documentId, fileModel.Name, sourceText, filePath: fileModel.Path),
			};
			return newSolution;
		}, WorkspaceChangeKind.DocumentAdded, documentId: documentId);
		return true;
	}

	public async Task<bool> RemoveDocument(SharpIdeFile fileModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(AddDocument)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(fileModel, nameof(fileModel));

		var documentIdsWithFilePath = _workspace!.CurrentSolution.GetDocumentIdsWithFilePath(fileModel.Path);
		var documentId = documentIdsWithFilePath switch
		{
			{Length: 1} => documentIdsWithFilePath[0],
			{Length: > 1} => documentIdsWithFilePath.SingleOrDefault(d => d.ProjectId == GetProjectForSharpIdeFile(fileModel).Id),
			_ => null
		};
		if (documentId is null)
		{
			_logger.LogWarning("RemoveDocument failed: Document '{DocumentPath}' not found in workspace", fileModel.Path);
			return false;
		}
		var documentKind = _workspace.CurrentSolution.GetDocumentKind(documentId);
		Guard.Against.Null(documentKind);

		switch (documentKind)
		{
			case TextDocumentKind.Document: _workspace.OnDocumentRemoved(documentId); break;
			case TextDocumentKind.AdditionalDocument: _workspace.OnAdditionalDocumentRemoved(documentId); break;
			case TextDocumentKind.AnalyzerConfigDocument: _workspace.OnAnalyzerConfigDocumentRemoved(documentId); break;
			default: throw new ArgumentOutOfRangeException(nameof(documentKind));
		}
		return true;
	}

	public async Task MoveDocument(SharpIdeFile sharpIdeFile, string oldFilePath)
	{
		var documentId = _workspace!.CurrentSolution.GetDocumentIdsWithFilePath(oldFilePath).Single();
		_workspace.SetCurrentSolution(oldSolution =>
		{
			var newSolution = oldSolution.WithDocumentFilePath(documentId, sharpIdeFile.Path);
			return newSolution;
		}, WorkspaceChangeKind.DocumentInfoChanged, documentId: documentId);
	}

	public async Task RenameDocument(SharpIdeFile sharpIdeFile, string oldFilePath)
	{
		var documentId = _workspace!.CurrentSolution.GetDocumentIdsWithFilePath(oldFilePath).Single();
		_workspace.SetCurrentSolution(oldSolution =>
		{
			var newSolution = oldSolution.WithDocumentName(documentId, sharpIdeFile.Name);
			return newSolution;
		}, WorkspaceChangeKind.DocumentInfoChanged, documentId: documentId);
	}

	public async Task<string> GetOutputDllPathForProject(SharpIdeProjectModel projectModel)
	{
		await _solutionLoadedTcs.Task;
		var project = GetProjectForSharpIdeProjectModel(projectModel);
		var outputPath = project.OutputFilePath;
		Guard.Against.NullOrWhiteSpace(outputPath);
		return outputPath;
	}

	private static SharpIdeProjectModel GetSharpIdeProjectForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		var sharpIdeProjectModel = ((IChildSharpIdeNode)sharpIdeFile).GetNearestProjectNode()!;
		return sharpIdeProjectModel;
	}

	private static Project GetProjectForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		var sharpIdeProjectModel = GetSharpIdeProjectForSharpIdeFile(sharpIdeFile);
		var project = GetProjectForSharpIdeProjectModel(sharpIdeProjectModel);
		return project;
	}

	private static Project GetProjectForSharpIdeProjectModel(SharpIdeProjectModel projectModel)
	{
		Guard.Against.Null(projectModel);
		var projectsForProjectPath = _workspace!.CurrentSolution.Projects.Where(s => s.FilePath == projectModel.FilePath).ToList();
		if (projectsForProjectPath.Count is 0) throw new InvalidOperationException($"No project found in workspace for project path '{projectModel.FilePath}'");
		if (projectsForProjectPath.Count is 1)
		{
			return projectsForProjectPath[0];
		}

		// Multiple projects with same path, different TFMs
		var projectAndFrameworkList = projectsForProjectPath
			.Select(s =>
			{
				var flavor = s.State.NameAndFlavor.flavor!;
				var framework = NuGetFramework.Parse(flavor);
				return (Project: s, Framework: framework);
			})
			.Where(s => s.Framework.IsDesktop() is false) // Exclude .NET Framework projects
			.ToList();

		if (projectAndFrameworkList.Any(s => s.Framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp)) // .NET Core project // I would prefer to use Framework.IsNet5Era
		{
			// remove .net standard projects
			projectAndFrameworkList = projectAndFrameworkList
				.Where(s => s.Framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp)
				.ToList();
		}

		var selectedProject = projectAndFrameworkList
			.OrderByDescending(s => s.Framework, NuGetFrameworkSorter.Instance)
			.Select(s => s.Project)
			.First();

		return selectedProject;
	}
}
