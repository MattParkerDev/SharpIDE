using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Ardalis.GuardClauses;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.DecompiledSource;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PdbSourceDocument;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Remote.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Remote.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SignatureHelp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Analysis.FixLoaders;
using SharpIDE.Application.Features.Analysis.ProjectLoader;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Application.Features.Analysis.WorkspaceServices;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
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
	private static IMetadataAsSourceFileService _metadataAsSourceFileService = null!;
	private static IImplementationAssemblyLookupService _implementationAssemblyLookupService = null!;
	private static SignatureHelpService _signatureHelpService = null!;
	private static HashSet<CodeRefactoringProvider> _codeRefactoringProviders = [];
	private static HashSet<CodeFixProvider> _codeFixProviders = [];
	private static readonly ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _compilationWithAnalyzersCache = new();

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
				CreateWorkspace();
				await LoadSolutionInWorkspace(solutionModel);
				var diagnosticsTask = UpdateSolutionDiagnostics();
				LoadCodeActions();
				await diagnosticsTask;
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
		using (var ___ = SharpIdeOtel.Source.StartActivity("RestoreSolution"))
		{
			// MsBuildProjectLoader doesn't do a restore which is absolutely required for resolving PackageReferences, if they have changed. I am guessing it just reads from project.assets.json
			// It is important to note that a Workspace has no concept of MSBuild, nuget packages etc. It is just told about project references and "metadata" references, which are dlls. This is what MSBuild does - it reads the csproj, and most importantly resolves nuget package references to dlls
			await _buildService.MsBuildAsync(solutionFilePath, BuildType.Restore, BuildStartedFlags.UserFacing, cancellationToken);
		}

		using (var ___ = SharpIdeOtel.Source.StartActivity("OpenSolution"))
		{
			//_msBuildProjectLoader!.LoadMetadataForReferencedProjects = true;

			// This call is the expensive part - MSBuild is slow. There doesn't seem to be any incrementalism for solutions.
			// The best we could do to speed it up is do .LoadProjectInfoAsync for the single project, and somehow munge that into the existing solution
			var (solutionInfo, projectFileInfos) = await _msBuildProjectLoader!.LoadSolutionInfoAsync(solutionFilePath, cancellationToken: cancellationToken);
			_projectFileInfoMap = projectFileInfos;

			var analyzerReferencePaths = solutionInfo.Projects
				.SelectMany(p => p.AnalyzerReferences.OfType<IsolatedAnalyzerFileReference>().Select(a => a.FullPath))
				.OfType<string>()
				.Distinct()
				.ToImmutableArray();
			await _analyzerFileWatcher.StartWatchingFiles(analyzerReferencePaths);

			// There doesn't appear to be any noticeable difference between ClearSolution + AddSolution vs OnSolutionReloaded
			//_workspace.OnSolutionReloaded(newSolutionInfo);
			_workspace!.ClearSolution();
			_workspace.AddSolution(solutionInfo);

			// If these aren't added, IDiagnosticAnalyzerService will not return compiler analyzer diagnostics
			// Note that we aren't currently using IDiagnosticAnalyzerService
			//var solutionAnalyzerReferences = CreateSolutionLevelAnalyzerReferencesForWorkspace(_workspace);
			//solution = solution.WithAnalyzerReferences(solutionAnalyzerReferences);
			//_workspace.SetCurrentSolution(solution);
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

		_msBuildProjectLoader = new CustomMsBuildProjectLoader(_workspace);
	}

	private static void LoadCodeActions()
	{
		using var _ = SharpIdeOtel.Source.StartActivity();
		if (_codeFixProviders.Count is not 0 || _codeRefactoringProviders.Count is not 0) return;
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

	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public async Task ReloadSolution(CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadSolution)}");
		_logger.LogInformation("RoslynAnalysis: Reloading solution");
		await _solutionLoadedTcs.Task;
		_solutionLoadedTcs = new TaskCompletionSource();
		Guard.Against.Null(_workspace, nameof(_workspace));
		Guard.Against.Null(_msBuildProjectLoader, nameof(_msBuildProjectLoader));

		await LoadSolutionCoreAsync(_sharpIdeSolutionModel!.FilePath, cancellationToken);

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
		var oldSolution = _workspace.CurrentSolution;

		// we can reliably rely on the Solution's graph of project inter-references, as a project has only been reloaded - no projects have been added or removed from the solution
		var dependentProjects = oldSolution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(thisProject.Id);
		var projectPathsToReload = dependentProjects.Select(id => oldSolution.GetProject(id)!.FilePath!).Append(thisProject.FilePath!).Distinct().ToList();
		//var projectMap = ProjectMap.Create(oldSolution); // using a projectMap may speed up LoadProjectInfosAsync, TODO: test
		// This will get all projects necessary to build this group of projects, regardless of whether those projects are actually affected by the original project change
		// We can potentially optimise this, but given this is the expensive part, lets just proceed with reloading them all in the solution
		// We potentially lose performance because Workspace/Solution caches are dropped, but lets not prematurely optimise
		var (loadedProjectInfos, projectFileInfos) = await _msBuildProjectLoader.LoadProjectInfosAsync(projectPathsToReload.ToImmutableArray(), null, null, cancellationToken);
		foreach (var (projectId, projectFileInfo) in projectFileInfos)
		{
			_projectFileInfoMap[projectId] = projectFileInfo;
		}
		__?.Dispose();

		var ___ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.Workspace.UpdateSolution");

		var oldProjectIdFilePathMap = oldSolution.Projects.ToDictionary(keySelector: static p => (p.FilePath!, p.Name), elementSelector: static p => p.Id);

		var projectIdMap = loadedProjectInfos.ToDictionary(
			keySelector: static info => info.Id,
			elementSelector: info => oldProjectIdFilePathMap.GetValueOrDefault((info.FilePath!, info.Name)));

		var newSolution = oldSolution;

		// When we "reload" a project, we assume: no projects have been removed from the solution, and none added (TODO: Consider/handle a project gaining a project reference to a project outside of the solution)
		// Therefore, loadedProjectInfos ⊆ (is a subset of) _workspace.CurrentSolution.Projects
		// The ProjectIds will not match however, so we need to match on FilePath
		// Since the ProjectIds don't match, we also need to remap all ProjectReferences to the existing ProjectIds
		// same for documents
		foreach (var newProjectInfo in loadedProjectInfos)
		{
			Contract.ThrowIfNull(newProjectInfo.FilePath);

			var oldProjectId = projectIdMap[newProjectInfo.Id];
			if (oldProjectId == null)
			{
				newSolution = newSolution.AddProject(newProjectInfo);
				continue;
			}

			newSolution = newSolution.WithProjectInfo(ProjectInfo.Create(
					oldProjectId,
					newProjectInfo.Version,
					newProjectInfo.Name,
					newProjectInfo.AssemblyName,
					newProjectInfo.Language,
					newProjectInfo.FilePath,
					newProjectInfo.OutputFilePath,
					newProjectInfo.CompilationOptions,
					newProjectInfo.ParseOptions,
					MapDocuments(oldProjectId, newProjectInfo.Documents),
					newProjectInfo.ProjectReferences.Select(MapProjectReference),
					newProjectInfo.MetadataReferences,
					newProjectInfo.AnalyzerReferences,
					MapDocuments(oldProjectId, newProjectInfo.AdditionalDocuments),
					isSubmission: false,
					hostObjectType: null,
					outputRefFilePath: newProjectInfo.OutputRefFilePath)
				.WithChecksumAlgorithm(newProjectInfo.ChecksumAlgorithm)
				.WithAnalyzerConfigDocuments(MapDocuments(oldProjectId, newProjectInfo.AnalyzerConfigDocuments))
				.WithCompilationOutputInfo(newProjectInfo.CompilationOutputInfo));
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

		ProjectReference MapProjectReference(ProjectReference oldRef)
		{
			// Only C# and VB projects are loaded by the MSBuildProjectLoader, so some references might be missing.
			// When a new project is added along with a new project reference the old project id is also null.
			return new(
				projectId: projectIdMap.TryGetValue(oldRef.ProjectId, out var oldProjectId) && oldProjectId != null ? oldProjectId : oldRef.ProjectId,
				aliases: oldRef.Aliases,
				embedInteropTypes: oldRef.EmbedInteropTypes);
		}
		ImmutableArray<DocumentInfo> MapDocuments(ProjectId mappedProjectId, IReadOnlyList<DocumentInfo> documents)
			=> documents.Select(docInfo =>
			{
				// Map to a document of the same path. If there isn't one (a new document is added to the project),
				// create a new document id with the mapped project id.
				var mappedDocumentId = oldSolution.GetDocumentIdsWithFilePath(docInfo.FilePath).FirstOrDefault(id => id.ProjectId == mappedProjectId)
				                       ?? DocumentId.CreateNewId(mappedProjectId);

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
		foreach (var project in _sharpIdeSolutionModel!.AllProjects.ToList()) // .ToList to avoid 'Object synchronization method was called from an unsynchronized block of code'
		{
			await UpdateProjectDiagnostics(project, cancellationToken);
		}
		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Solution diagnostics updated in {ElapsedMilliseconds}ms", timer.ElapsedMilliseconds);
	}

	public async Task UpdateProjectDiagnostics(SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		if (project.IsLoaded is false) return;
		var projectDiagnostics = await GetProjectDiagnostics(project, cancellationToken);
		ApplyProjectDiagnosticsDiff(project, projectDiagnostics);
	}

	private static readonly Lock _applyProjectDiagnosticsLock = new();
	/// Only removes diagnostics that went away and adds ones that are new, so subscribers (open tabs, the Problems panel) don't tear down
	/// and rebuild everything on every update. Uses SharpIdeDiagnostic's value equality, which delegates to Roslyn's Diagnostic.Equals
	/// (location + message arguments). That means:
	/// - a diagnostic in an unedited file with only string arguments is equal across compilations (unchanged syntax trees are reused), so it is kept
	/// - one with symbol arguments is never equal across compilations, so it is replaced - which conveniently means we never keep an
	///   old instance that would hold an outdated Compilation alive via its symbols
	private static void ApplyProjectDiagnosticsDiff(SharpIdeProjectModel project, ImmutableArray<SharpIdeDiagnostic> newDiagnostics)
	{
		// Serialise concurrent applies (e.g. the focused tab's fast path and the batched solution pass), so they can't interleave mid-diff
		lock (_applyProjectDiagnosticsLock)
		{
			var newSet = newDiagnostics.ToHashSet();
			var toRemove = project.Diagnostics.Where(d => newSet.Contains(d) is false).ToList();
			newSet.ExceptWith(project.Diagnostics);
			if (toRemove.Count is not 0) project.Diagnostics.RemoveRange(toRemove);
			if (newSet.Count is not 0) project.Diagnostics.AddRange(newSet);
		}
	}

	public async Task UpdateProjectDiagnosticsForFile(SharpIdeFile sharpIdeFile, CancellationToken cancellationToken = default)
	{
		var project = GetRequiredSharpIdeProjectForSharpIdeFile(sharpIdeFile);
		Guard.Against.Null(project);
		await UpdateProjectDiagnostics(project, cancellationToken);
	}

	/// Everything a project's compiler diagnostics depend on:
	/// - ProjectVersion: options, references, .editorconfig, documents added/removed
	/// - LatestDocumentVersion: any edit to this project's own documents/additional documents, including method bodies
	/// - DependentSemanticVersion: declaration-level (signature) changes in referenced projects. Method-body edits there don't count, as
	///   referenced projects are compiled against as skeleton (declarations only) references, which Roslyn itself reuses while this version is unchanged
	private readonly record struct ProjectDiagnosticsKey(VersionStamp ProjectVersion, VersionStamp LatestDocumentVersion, VersionStamp DependentSemanticVersion);
	private sealed record ProjectDiagnosticsEntry(ProjectDiagnosticsKey Key, Task<ImmutableArray<SharpIdeDiagnostic>> Task, CancellationTokenSource Cts);
	private readonly Dictionary<ProjectId, ProjectDiagnosticsEntry> _projectDiagnosticsCache = new();
	private readonly Lock _projectDiagnosticsCacheLock = new();

	/// Cached per project, and shared between concurrent callers (e.g. the focused tab's fast path and the batched solution-wide pass),
	/// so a given project version is only ever computed once.
	public async Task<ImmutableArray<SharpIdeDiagnostic>> GetProjectDiagnostics(SharpIdeProjectModel projectModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetProjectDiagnostics)}");
		await _solutionLoadedTcs.Task;
		while (true)
		{
			var project = GetProjectForSharpIdeProjectModel(projectModel);
			var key = new ProjectDiagnosticsKey(project.Version, await project.GetLatestDocumentVersionAsync(cancellationToken), await project.GetDependentSemanticVersionAsync(cancellationToken));

			ProjectDiagnosticsEntry entry;
			ProjectDiagnosticsEntry? supersededEntry = null;
			lock (_projectDiagnosticsCacheLock)
			{
				if (_projectDiagnosticsCache.TryGetValue(project.Id, out var existing) && existing.Key == key && existing.Task is not { IsCanceled: true } and not { IsFaulted: true })
				{
					entry = existing;
				}
				else
				{
					supersededEntry = existing;
					// Deliberately not the caller's token: other callers may share this task. It is cancelled only when superseded by a newer version.
					var cts = new CancellationTokenSource();
					entry = new ProjectDiagnosticsEntry(key, Task.Run(() => ComputeProjectDiagnostics(project, projectModel.FilePath, cts.Token), CancellationToken.None), cts);
					_projectDiagnosticsCache[project.Id] = entry;
				}
			}
			// The superseded computation is for an outdated version, and nobody should receive its result - stop it wasting CPU
			supersededEntry?.Cts.Cancel();

			try
			{
				return await entry.Task.WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
			{
				// The shared computation was superseded by a newer version while we waited. Retry against the current version.
			}
		}
	}

	private static async Task<ImmutableArray<SharpIdeDiagnostic>> ComputeProjectDiagnostics(Project project, string projectFilePath, CancellationToken cancellationToken)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ComputeProjectDiagnostics)}");
		var compilation = await project.GetCompilationAsync(cancellationToken);
		Guard.Against.Null(compilation, nameof(compilation));

		var allDiagnostics = compilation.GetDiagnostics(cancellationToken);
		var diagnostics = allDiagnostics
			.Where(d => d.Severity is not DiagnosticSeverity.Hidden)
			.Select(d =>
			{
				var mappedFileLinePositionSpan = d.Location.IsInSource
					? d.Location.SourceTree!.GetMappedLineSpan(d.Location.SourceSpan)
					: new FileLinePositionSpan(projectFilePath, new LinePositionSpan(LinePosition.Zero, LinePosition.Zero));
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return diagnostics;
	}

	public async Task<ImmutableArray<SharpIdeDiagnostic>> GetDocumentDiagnostics(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return [];
		if (fileModel.IsMetadataAsSourceFile) return []; // Due to the current nature of IMetadataAsSourceFileService, which only decompiles a single document (not the whole project), if this was enabled, we would get error diagnostics for missing references to other "files" in the same dll
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
		if (fileModel.IsMetadataAsSourceFile) return []; // Decompiled/SourceLink files should not/will not have analyzers
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentAnalyzerDiagnostics)}");
		await _solutionLoadedTcs.Task;

		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document, nameof(document));

		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		// Cached per Compilation: CompilationWithAnalyzers clones the compilation (losing its binding caches) and remembers which analyzers have already run on which trees.
		// Reusing it means repeated requests against an unchanged compilation return stored results instead of re-running every analyzer from scratch.
		var compilationWithAnalyzers = _compilationWithAnalyzersCache.GetValue(semanticModel.Compilation, compilation =>
		{
			var projectAnalyzers = document.Project.AnalyzerReferences
				.OfType<IsolatedAnalyzerFileReference>()
				.SelectMany(r => r.GetAnalyzers(document.Project.Language))
				.ToImmutableArray();
			return compilation.WithAnalyzers(projectAnalyzers);
		});

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

	/// Returns a version that changes whenever anything that could affect this file's highlighting or diagnostics changes:
	/// any document or additional (e.g. razor) document in its project, the project itself, or any project it references.
	/// Returns null if the file is not part of a loaded workspace project - callers should then assume it has changed.
	public async Task<VersionStamp?> GetFileAnalysisVersion(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return null;
		await _solutionLoadedTcs.Task;
		Project project;
		try
		{
			project = GetProjectForSharpIdeFile(fileModel);
		}
		catch (Exception e) when (e is InvalidOperationException or ArgumentException)
		{
			return null;
		}
		// Deliberately not GetDependentSemanticVersionAsync - that only tracks top-level (signature) changes, so it would not change for method body edits
		return await project.GetDependentVersionAsync(cancellationToken);
	}

	private async Task<Document> GetDocumentForSharpIdeFile(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
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
		if (fileModel.IsRazorFile is false)
		{
			return [];
			//throw new InvalidOperationException("File is not a .razor file");
		}
		var project = GetProjectForSharpIdeFile(fileModel);
		var razorDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(razorDocument);

		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);

		var razorText = await razorDocument.GetTextAsync(cancellationToken);

		List<string> relevantTypes = ["razorDirective", "razorTransition", "markupTextLiteral", "markupTagDelimiter", "markupElement", "razorComponentElement", "razorComponentAttribute", "razorComment", "razorCommentTransition", "razorCommentStar", "markupOperator", "markupAttributeQuote"];
		var ranges = new List<SemanticRange>();
		CustomSemanticTokensVisitor.AddSemanticRanges(ranges, razorCodeDocument, new TextSpan(0, razorText.Length), _semanticTokensLegendService!, false);

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
		// Razor now emits two C# documents ("sonic"): the impl document (markup/render tree, expressions etc), and the
		// declaration document (e.g. @code block members). Each has its own source generated document, so classify both,
		// mirroring RazorSemanticTokensInfoService.AddCSharpSemanticRangesAsync
		List<SharpIdeRazorMappedClassifiedSpan> roslynMappedSpans = [];
		roslynMappedSpans.AddRange(await GetMappedClassifiedSpans(
			razorCodeDocument.GetRequiredCSharpDocument(false),
			await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken)));

		var declCSharpDocument = razorCodeDocument.GetCSharpDocument(true);
		var declGeneratedDocument = declCSharpDocument is null ? null : await razorProjectSnapshot.TryGetDeclGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		if (declCSharpDocument is not null && declGeneratedDocument is not null)
		{
			roslynMappedSpans.AddRange(await GetMappedClassifiedSpans(declCSharpDocument, declGeneratedDocument));
		}

		List<SharpIdeRazorClassifiedSpan> sharpIdeRazorSpans = [
			..roslynMappedSpans.Select(s => new SharpIdeRazorClassifiedSpan(s.SourceSpanInRazor, SharpIdeRazorSpanKind.Code, s.CsharpClassificationType)),
			..semanticRangeRazorSpans
		];
		// Remove exact duplicates, which are an artifact of mapping generated C# back to razor, e.g. a component's T="Foo"
		// is emitted several times in the generated C#, and some spans map from both the impl and declaration documents.
		var result = sharpIdeRazorSpans.Distinct().OrderBy(s => s.Span.AbsoluteIndex).ToImmutableArray();
		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Razor syntax highlighting for {FileName} took {ElapsedMilliseconds}ms", fileModel.Name.Value, timer.ElapsedMilliseconds);
		return result;

		async Task<List<SharpIdeRazorMappedClassifiedSpan>> GetMappedClassifiedSpans(RazorCSharpDocument razorCSharpDocument, SourceGeneratedDocument generatedDocument)
		{
			var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);
			var sourceMappings = razorCSharpDocument.SourceMappingsSortedByOriginal.Select(s => s.ToSharpIdeSourceMapping()).ToImmutableArray();
			var classifiedSpans = await Classifier.GetClassifiedSpansAsync(generatedDocument, generatedDocSyntaxRoot!.FullSpan, cancellationToken);
			var mappedSpans = new List<SharpIdeRazorMappedClassifiedSpan>();
			foreach (var s in classifiedSpans)
			{
				var genSpan = s.TextSpan;
				var mapping = sourceMappings.FirstOrDefault(m => m.GeneratedSpan.AsTextSpan().IntersectsWith(genSpan));
				if (mapping is null) continue;

				// Translate generated span back to Razor span
				var offset = genSpan.Start - mapping.GeneratedSpan.AbsoluteIndex;
				var mappedStart = mapping.OriginalSpan.AbsoluteIndex + offset;
				var razorLine = razorText.Lines.GetLineFromPosition(mappedStart);
				var characterIndex = mappedStart - razorLine.Start;
				var sharpIdeSpan = new SharpIdeRazorSourceSpan(
					mapping.OriginalSpan.FilePath,
					mappedStart,
					razorLine.LineNumber,
					characterIndex,
					genSpan.Length,
					1,
					characterIndex + genSpan.Length
				);
				mappedSpans.Add(new SharpIdeRazorMappedClassifiedSpan(sharpIdeSpan, s.ClassificationType));
			}
			return mappedSpans;
		}
	}

	// This is expensive for files that have just been updated, making it suboptimal for real-time highlighting
	public async Task<ImmutableArray<SharpIdeClassifiedSpan>> GetDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		if (fileModel.IsCsharpFile is false)
		{
			//throw new InvalidOperationException("File is not a .cs");
			return [];
		}

		var project = GetProjectForSharpIdeFile(fileModel);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));

		var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
		var root = await syntaxTree!.GetRootAsync(cancellationToken);

		var classifiedSpans = await Classifier.GetClassifiedSpansAsync(document, root.FullSpan, cancellationToken);
		//var classifiedSpans = await ClassifierHelper.GetClassifiedSpansAsync(document, root.FullSpan, ClassificationOptions.Default, true, cancellationToken);
		var result = classifiedSpans.Select(s => new SharpIdeClassifiedSpan(syntaxTree.GetMappedLineSpan(s.TextSpan).Span, s)).ToImmutableArray();
		return result;
	}

	public async Task<DebuggerExpressionIntelliSenseSession?> CreateDebuggerExpressionIntelliSenseSessionAsync(
		SharpIdeFile fileModel,
		LinePosition debuggerStoppedPosition,
		CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(CreateDebuggerExpressionIntelliSenseSessionAsync)}");
		await _solutionLoadedTcs.Task;
		if (fileModel.IsCsharpFile is false)
		{
			return null;
		}

		var project = GetProjectForSharpIdeFile(fileModel);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document);
		return await DebuggerExpressionIntelliSenseSession.CreateAsync(document, debuggerStoppedPosition, cancellationToken);
	}

	// We store the document here, so that we have the correct version of the document when we compute completions
	// This may not be the best way to do this, but it seems to work okay. It may only be a problem because I continue to update the doc in the workspace as the user continues typing, filtering the completion
	// I could possibly pause updating the document while the completion list is open, but that seems more complex - handling accepted vs cancelled completions etc
	public record IdeCompletionListResult(Document Document, CompletionList CompletionList, LinePosition LinePosition);
	public async Task<IdeCompletionListResult> GetCodeCompletionsForDocumentAtPosition(SharpIdeFile fileModel, SourceText documentText, LinePosition linePosition, CompletionTrigger completionTrigger)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeCompletionsForDocumentAtPosition)}");
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(fileModel);
		Guard.Against.Null(document, nameof(document));
		// The workspace may not have caught up with the editor yet (or may have moved past this text), so fork a document with the text at the time of the completion request.
		// documentText is the editor's own SourceText, so when committing, edits typed since can be mapped forward via GetChangeRanges.
		document = document.WithText(documentText);
		var (completions, triggerLinePosition) = await GetCompletionsAsync(document, linePosition, completionTrigger).ConfigureAwait(false);
		return new IdeCompletionListResult(document, completions, triggerLinePosition);
	}

	public static async Task<CompletionDescription> GetCompletionDescription(Document document, CompletionItem completionItem, CancellationToken cancellationToken = default)
	{
		var completionService = CompletionService.GetService(document) ?? throw new InvalidOperationException("Completion service is not available for the document.");
		return (await completionService.GetDescriptionAsync(document, completionItem, cancellationToken))!;
	}

	public async Task<SharpIdeSignatureHelpItems?> GetMethodSignatureInfo(SharpIdeFile file, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(file, cancellationToken);
		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.Lines.GetPosition(linePosition);

		// var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken);
		// var syntaxRoot = await document.GetRequiredSyntaxRootAsync(cancellationToken);
		// var token = syntaxRoot.FindToken(position);
		// var argumentListSyntax = token.Parent;
		// var invocationExpressionSyntax = argumentListSyntax?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
		// if (invocationExpressionSyntax is null) return null;
		// var symbolInfo = semanticModel.GetSymbolInfo(invocationExpressionSyntax, cancellationToken);
		// var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
		// if (symbol is not IMethodSymbol methodSymbol) return null;

		var triggerInfo = new SignatureHelpTriggerInfo(SignatureHelpTriggerReason.InvokeSignatureHelpCommand);
		var (_, signatureHelpItems) = await _signatureHelpService.GetSignatureHelpAsync(document, position, triggerInfo, cancellationToken);
		if (signatureHelpItems is null) return null;

		var applicableSpan = signatureHelpItems.ApplicableSpan;
		var mappedLineSpan = (await document.GetRequiredSyntaxTreeAsync(cancellationToken)).GetMappedLineSpan(applicableSpan, cancellationToken);
		var mappedLinePositionSpan = mappedLineSpan.Span;

		var sharpIdeSignatureHelpItems = new SharpIdeSignatureHelpItems
		{
			Items =  signatureHelpItems,
			ApplicableSpan = mappedLinePositionSpan
		};

		return sharpIdeSignatureHelpItems;

		// var symbols = semanticModel.LookupSymbols(position, methodSymbol.ReceiverType, methodSymbol.Name, true)
		// 	.OfType<IMethodSymbol>()
		// 	.ToImmutableArray();
		// return symbols;
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

	public async Task<bool> ShouldTriggerCompletionAsync(SharpIdeFile file, SourceText sourceText, LinePosition linePosition, CompletionTrigger completionTrigger, CancellationToken cancellationToken = default)
	{
		await _solutionLoadedTcs.Task;
		if (file.IsRoslynWorkspaceFile is false) return false;
		var document = await GetDocumentForSharpIdeFile(file, cancellationToken);
		var completionService = CompletionService.GetService(document);
		if (completionService is null) throw new InvalidOperationException("Completion service is not available for the document.");

		var position = sourceText.Lines.GetPosition(linePosition);
		var shouldTrigger = completionService.ShouldTriggerCompletion(document.Project, document.Project.Services, sourceText, position, completionTrigger, CompletionOptions.Default, document.Project.Solution.Options ?? OptionSet.Empty);
		return shouldTrigger;
	}

	public static ImmutableArray<SharpIdeCompletionItem> FilterCompletions(SourceText sourceText, LinePosition linePosition, CompletionList completionList, CompletionTrigger completionTrigger, CompletionFilterReason filterReason)
	{
		return FilterCompletions(sourceText, sourceText.Lines.GetPosition(linePosition), completionList.Span.Start, completionList, completionTrigger, filterReason);
	}

	internal static ImmutableArray<SharpIdeCompletionItem> FilterCompletions(
		SourceText sourceText,
		int position,
		int completionSpanStart,
		CompletionList completionList,
		CompletionTrigger completionTrigger,
		CompletionFilterReason filterReason)
	{
		if (completionSpanStart < 0 || completionSpanStart > sourceText.Length || position < completionSpanStart || position > sourceText.Length)
		{
			return [];
		}

		var filterSpanLength = position - completionSpanStart;
		var filterSpan = new TextSpan(completionSpanStart, length: filterSpanLength);

		var filteredCompletionItems = FilterCompletionList(completionList, filterSpan, completionTrigger, filterReason, sourceText);
		return filteredCompletionItems;
	}

	private static ImmutableArray<SharpIdeCompletionItem> FilterCompletionList(CompletionList completionList, TextSpan filterSpan, CompletionTrigger completionTrigger, CompletionFilterReason filterReason, SourceText sourceText)
    {
        var filterText = sourceText.GetSubText(filterSpan).ToString();

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

	/// The change for committing a completion item, relative to the text of <paramref name="document"/> (the document the completion list was computed for).
	/// NewCaretPosition is in the coordinates of that text with the change applied.
	public static async Task<(TextChange Change, int NewCaretPosition)> GetCompletionChange(CompletionItem completionItem, Document document, CancellationToken cancellationToken = default)
	{
		var completionService = CompletionService.GetService(document) ?? throw new InvalidOperationException("Completion service is not available for the document.");
		var completionChange = await completionService.GetChangeAsync(document, completionItem, commitCharacter: '.', cancellationToken: cancellationToken);
		var newCaretPosition = completionChange.NewPosition ?? NewCaretPosition();
		return (completionChange.TextChange, newCaretPosition);

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
	public async Task<List<SharpIdeFileTextEdit>> GetCodeActionApplyChanges(CodeAction codeAction, CancellationToken cancellationToken = default)
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

		return await GetSolutionTextEdits(originalSolution, updatedSolution, cancellationToken);
	}

	/// The minimal text edits that turn oldSolution into newSolution - one per changed file, as linked documents (e.g. one per TFM) share a file
	private async Task<List<SharpIdeFileTextEdit>> GetSolutionTextEdits(Solution oldSolution, Solution newSolution, CancellationToken cancellationToken = default)
	{
		// TODO: Handle added and removed documents
		var edits = new List<SharpIdeFileTextEdit>();
		var seenFilePaths = new HashSet<string>();
		foreach (var projectChanges in newSolution.GetChanges(oldSolution).GetProjectChanges())
		{
			var changedDocumentIds = projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true).Concat(projectChanges.GetChangedAdditionalDocuments());
			foreach (var documentId in changedDocumentIds)
			{
				var oldDocument = oldSolution.GetTextDocument(documentId);
				var newDocument = newSolution.GetTextDocument(documentId);
				if (oldDocument?.FilePath is not { } filePath || newDocument is null) continue;
				if (seenFilePaths.Add(filePath) is false) continue;

				var oldText = await oldDocument.GetTextAsync(cancellationToken);
				// For documents, Roslyn can diff the syntax trees, giving precise changes
				var changes = (oldDocument, newDocument) is (Document oldDoc, Document newDoc)
					? (await newDoc.GetTextChangesAsync(oldDoc, cancellationToken)).ToImmutableArray()
					: (await newDocument.GetTextAsync(cancellationToken)).GetTextChanges(oldText).ToImmutableArray();
				if (changes.IsEmpty) continue;

				var file = _sharpIdeSolutionModel!.AllFiles[filePath];
				edits.Add(new SharpIdeFileTextEdit(file, oldText, changes));
			}
		}
		return edits;
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
	public async Task<List<SharpIdeFileTextEdit>> GetRenameApplyChanges(ISymbol symbol, string newName, CancellationToken cancellationToken = default)
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
		return await GetSolutionTextEdits(currentSolution, newSolution, cancellationToken);
	}

	public async Task<string?> GetMetadataAsSourceFromDebuggingAssemblyAndType(string typeName, string assemblyPath, Guid mvid, string userCodeCallingAssemblyPath, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetMetadataAsSourceFromDebuggingAssemblyAndType)}");
		await _solutionLoadedTcs.Task;

		var callingProject = _workspace!.CurrentSolution.Projects.SingleOrDefault(p => p.OutputFilePath == userCodeCallingAssemblyPath);
		if (callingProject is null) return null;

		var runtimeReference = MetadataReference.CreateFromFile(assemblyPath);
		if (runtimeReference.GetMetadata() is not AssemblyMetadata runtimeMetadata || runtimeMetadata.GetMvid() != mvid) return null;

		// Read the full identity from the exact runtime assembly before adding it to the calling project's compilation.
		var identityCompilation = CSharpCompilation.Create(assemblyName: null, references: [runtimeReference], options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		if (identityCompilation.GetAssemblyOrModuleSymbol(runtimeReference) is not IAssemblySymbol runtimeAssemblyForIdentity) return null;

		var compilation = await callingProject.GetRequiredCompilationAsync(cancellationToken);
		var conflictingReferences = callingProject.MetadataReferences
			.Where(reference => compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly && assembly.Identity.Equals(runtimeAssemblyForIdentity.Identity))
			.ToImmutableArray();
		if (conflictingReferences.IsEmpty is false)
		{
			runtimeReference = runtimeReference.WithProperties(conflictingReferences[0].Properties);
			foreach (var conflictingReference in conflictingReferences)
			{
				callingProject = callingProject.RemoveMetadataReference(conflictingReference);
			}
		}

		callingProject = callingProject.AddMetadataReference(runtimeReference);
		compilation = await callingProject.GetRequiredCompilationAsync(cancellationToken);
		var runtimeAssembly = compilation.GetAssemblyOrModuleSymbol(runtimeReference) as IAssemblySymbol;
		var symbol = runtimeAssembly?.GetTypeByMetadataName(typeName);
		if (symbol is null) return null;

		var options = MetadataAsSourceOptions.Default;// with { NavigateToSourceLinkAndEmbeddedSources = false };
		var metadataAsSourceFile = await _metadataAsSourceFileService.GetGeneratedFileAsync(_workspace, callingProject, symbol, false, options, cancellationToken);
		return metadataAsSourceFile.FilePath;
	}

	public async Task<string?> WriteSourceFromMetadataAsSourceWorkspaceToDisk(string filePath, CancellationToken cancellationToken = default)
	{
		await _solutionLoadedTcs.Task;
		var metadataAsSourceWorkspace = _metadataAsSourceFileService.TryGetWorkspace();
		var documentId = metadataAsSourceWorkspace!.CurrentSolution.GetDocumentIdsWithFilePath(filePath).SingleOrDefault();
		var document = metadataAsSourceWorkspace.CurrentSolution.GetDocument(documentId);
		if (document is null) return null;
		await DecompileWholeAssemblyToProjectMetadataAsSourceFileProvider.WriteFileToDiskAsync(document, cancellationToken);
		return document.FilePath;
	}

	public async Task<(string FilePath, string PdbSourceFilePath, Location Location)?> GetMetadataAsSourceForSymbol(SharpIdeFile currentFile, ISymbol symbol, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(FindAllSymbolReferences)}");
		await _solutionLoadedTcs.Task;
		if (_metadataAsSourceFileService.IsNavigableMetadataSymbol(symbol) is false) return null;

		var documentContainingMetadataReference = await GetDocumentForSharpIdeFile(currentFile, cancellationToken);

		var options = MetadataAsSourceOptions.Default;// with { NavigateToSourceLinkAndEmbeddedSources = false };
		var metadataAsSourceFile = await _metadataAsSourceFileService.GetGeneratedFileAsync(_workspace!, documentContainingMetadataReference.Project, symbol, false, options, cancellationToken);
		var metadataAsSourceWorkspace = _metadataAsSourceFileService.TryGetWorkspace();
		var documentId = metadataAsSourceWorkspace!.CurrentSolution.GetDocumentIdsWithFilePath(metadataAsSourceFile.FilePath).SingleOrDefault();
		if (documentId is null) return null;
		var document = await metadataAsSourceWorkspace.CurrentSolution.GetRequiredDocumentAsync(documentId, cancellationToken);
		if (document.FilePath is null) return null;
		return (document.FilePath, document.Name, metadataAsSourceFile.IdentifierLocation);
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

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);
		var razorAbsoluteIndex = razorText.Lines.GetPosition(linePosition);
		var mapped = await MapRazorPositionToGeneratedCSharp(razorProjectSnapshot, documentSnapshot, razorAbsoluteIndex, cancellationToken);
		if (mapped is null) return (null, null);
		var (razorCSharpDocument, generatedDocument, mappedPosition) = mapped.Value;
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var semanticModelAsync = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModelAsync!, generatedDocSyntaxRoot!, mappedPosition);
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

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);
		var razorAbsoluteIndex = razorText.Lines.GetPosition(linePosition);
		var mapped = await MapRazorPositionToGeneratedCSharp(razorProjectSnapshot, documentSnapshot, razorAbsoluteIndex, cancellationToken);
		if (mapped is null) return (null, null, null);
		var (_, generatedDocument, mappedPosition) = mapped.Value;
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var semanticModel = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModel!, generatedDocSyntaxRoot!, mappedPosition);

		var semanticInfo = await SymbolFinder.GetSemanticInfoAtPositionAsync(semanticModel!, mappedPosition, generatedDocument.Project.Solution.Services, cancellationToken).ConfigureAwait(false);
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

	// Razor emits two C# documents - the impl document, and the declaration document (e.g. @code block members).
	// Map the razor position into whichever one contains it, and return the matching source generated document.
	private static async Task<(RazorCSharpDocument CSharpDocument, SourceGeneratedDocument GeneratedDocument, int CSharpIndex)?> MapRazorPositionToGeneratedCSharp(RemoteProjectSnapshot razorProjectSnapshot, RemoteDocumentSnapshot documentSnapshot, int razorAbsoluteIndex, CancellationToken cancellationToken)
	{
		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		if (DocumentMappingHelper.TryMapToCSharpDocumentLinePosition(razorCodeDocument, razorAbsoluteIndex, out _, out var csharpIndex, out var inDeclDocument) is false)
		{
			return null;
		}

		if (inDeclDocument)
		{
			var declGeneratedDocument = await razorProjectSnapshot.TryGetDeclGeneratedDocumentAsync(documentSnapshot, cancellationToken);
			if (declGeneratedDocument is null) return null;
			return (razorCodeDocument.GetCSharpDocument(true)!, declGeneratedDocument, csharpIndex);
		}

		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		return (razorCodeDocument.GetRequiredCSharpDocument(false), generatedDocument, csharpIndex);
	}

	/// Returns true if the document was found and its text changed, otherwise false.
	/// For incremental parsing, newText should descend (via WithChanges) from the text currently in the workspace.
	public async Task<bool> UpdateDocument(SharpIdeFile fileModel, SourceText newText)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(UpdateDocument)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(fileModel, nameof(fileModel));

		var currentSolution = _workspace!.CurrentSolution;
		var documentIds = currentSolution.GetDocumentIdsWithFilePath(fileModel.Path);
		if (documentIds.IsEmpty) return false;

		// Skip no-ops, e.g. an editor opening a file whose text the workspace already has. Usually cheap - differing lengths return immediately.
		if (currentSolution.GetTextDocument(documentIds[0]) is { } existingDocument &&
		    existingDocument.TryGetText(out var existingText) &&
		    (ReferenceEquals(existingText, newText) || existingText.ContentEquals(newText)))
		{
			return false;
		}

		// Atomic: the transformation is re-run against the latest solution if another change lands first, so concurrent updates can't be lost.
		// Linked documents (the same file in several projects, e.g. one per TFM) all get the new text.
		return _workspace.SetCurrentSolution(oldSolution =>
		{
			var newSolution = oldSolution;
			foreach (var documentId in oldSolution.GetDocumentIdsWithFilePath(fileModel.Path))
			{
				if (newSolution.ContainsDocument(documentId)) newSolution = newSolution.WithDocumentText(documentId, newText, PreservationMode.PreserveIdentity);
				else if (newSolution.ContainsAdditionalDocument(documentId)) newSolution = newSolution.WithAdditionalDocumentText(documentId, newText, PreservationMode.PreserveIdentity);
				else if (newSolution.ContainsAnalyzerConfigDocument(documentId)) newSolution = newSolution.WithAnalyzerConfigDocumentText(documentId, newText, PreservationMode.PreserveIdentity);
			}
			return newSolution;
		}, WorkspaceChangeKind.DocumentChanged, documentId: documentIds[0]);
	}

	public async Task<bool> AddDocument(SharpIdeFile fileModel, string content)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(AddDocument)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(fileModel, nameof(fileModel));
		Guard.Against.Null(content, nameof(content));

		var sharpIdeProject = GetSharpIdeProjectForSharpIdeFile(fileModel);
		if (sharpIdeProject is null) return false;
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
				{ IsCsharpFile: true } => _workspace.CurrentSolution.AddDocument(documentId, fileModel.Name.Value, sourceText, filePath: fileModel.Path),
				_ => _workspace.CurrentSolution.AddAdditionalDocument(documentId, fileModel.Name.Value, sourceText, filePath: fileModel.Path),
			};
			return newSolution;
		}, WorkspaceChangeKind.DocumentAdded, documentId: documentId);
		return true;
	}

	public async Task<bool> RemoveDocument(SharpIdeFile fileModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(RemoveDocument)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(fileModel, nameof(fileModel));

		var oldSolution = _workspace!.CurrentSolution;
		var documentIdsWithFilePath = oldSolution.GetDocumentIdsWithFilePath(fileModel.Path);

		var documentIds = documentIdsWithFilePath.Where(id => oldSolution.GetDocument(id) is not null).ToImmutableArray();
		var additionalDocumentIds = documentIdsWithFilePath.Where(id => oldSolution.GetAdditionalDocument(id) is not null).ToImmutableArray();
		var analyzerConfigDocumentIds = documentIdsWithFilePath.Where(id => oldSolution.GetAnalyzerConfigDocument(id) is not null).ToImmutableArray();

		var removedAnything =
			documentIds.IsEmpty is false ||
			additionalDocumentIds.IsEmpty is false ||
			analyzerConfigDocumentIds.IsEmpty is false;

		if (removedAnything is false)
		{
			_logger.LogWarning("RemoveDocument failed: Document '{DocumentPath}' not found in workspace", fileModel.Path);
			return false;
		}

		var updatedSolution = oldSolution
			.RemoveDocuments(documentIds)
			.RemoveAdditionalDocuments(additionalDocumentIds)
			.RemoveAnalyzerConfigDocuments(analyzerConfigDocumentIds);

		_workspace.SetCurrentSolution(updatedSolution);
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
			var newSolution = oldSolution.WithDocumentFilePath(documentId, sharpIdeFile.Path).WithDocumentName(documentId, sharpIdeFile.Name.Value);
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

	private SharpIdeProjectModel? GetSharpIdeProjectForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		var containingProjectFolder = sharpIdeFile.GetContainingProjectFolder();
		if (containingProjectFolder is null) return null;
		var sharpIdeProject = _sharpIdeSolutionModel!.GetProjectForContainingFolderPath(containingProjectFolder);
		return sharpIdeProject;
	}
	private SharpIdeProjectModel GetRequiredSharpIdeProjectForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		var containingProjectFolder = sharpIdeFile.GetContainingProjectFolder();
		Guard.Against.Null(containingProjectFolder);
		var sharpIdeProject = _sharpIdeSolutionModel!.GetProjectForContainingFolderPath(containingProjectFolder);
		Guard.Against.Null(sharpIdeProject);
		return sharpIdeProject;
	}

	private Project GetProjectForSharpIdeFile(SharpIdeFile sharpIdeFile)
	{
		if (sharpIdeFile.IsMetadataAsSourceFile)
		{
			var metadataAsSourceWorkspace = _metadataAsSourceFileService.TryGetWorkspace() ?? throw new InvalidOperationException("Metadata as source workspace is not available");
			var documentId = metadataAsSourceWorkspace.CurrentSolution.GetDocumentIdsWithFilePath(sharpIdeFile.Path).SingleOrDefault() ?? throw new InvalidOperationException($"Document with path '{sharpIdeFile.Path}' not found in metadata as source workspace");
			var document = metadataAsSourceWorkspace.CurrentSolution.GetDocument(documentId) ?? throw new InvalidOperationException($"Document with id '{documentId}' not found in metadata as source workspace");
			var metadataAsSourceProject = document.Project;
			return metadataAsSourceProject;
		}
		var sharpIdeProjectModel = GetRequiredSharpIdeProjectForSharpIdeFile(sharpIdeFile);
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

		var activeTargetFramework = projectModel.TargetFramework;
		return projectsForProjectPath.Single(project => string.Equals(project.State.NameAndFlavor.flavor, activeTargetFramework, StringComparison.OrdinalIgnoreCase));
	}
}

internal static class AssemblyMetadataExtensions
{
	public static Guid GetMvid(this AssemblyMetadata assemblyMetadata)
	{
		var module = assemblyMetadata.GetModules()[0];
		var reader = module.GetMetadataReader();

		var moduleDef = reader.GetModuleDefinition();
		var mvid = reader.GetGuid(moduleDef.Mvid);
		return mvid;
	}
}
