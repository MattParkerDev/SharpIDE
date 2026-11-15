using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.SolutionDiscovery;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
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
		// Files outside any project (e.g. a solution-level .editorconfig) have no project diagnostics of their own -
		// the projects they affect are updated by the solution-wide pass
		await _solutionLoadedTcs.Task;
		var workspaceProject = TryGetTextDocumentForSharpIdeFile(sharpIdeFile)?.Project;
		var project = workspaceProject is null ? null : GetSharpIdeProjectModelForProject(workspaceProject);
		if (project is null) return;
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
		Guard.Against.Null(compilation);

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

	/// Compiler diagnostics. Cheap - Roslyn caches them per compilation.
	public async Task<SharpIdeDocumentDiagnostics> GetDocumentDiagnostics(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return SharpIdeDocumentDiagnostics.Empty;
		if (fileModel.IsMetadataAsSourceFile) return SharpIdeDocumentDiagnostics.Empty; // Due to the current nature of IMetadataAsSourceFileService, which only decompiles a single document (not the whole project), if this was enabled, we would get error diagnostics for missing references to other "files" in the same dll
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentDiagnostics)}");
		await _solutionLoadedTcs.Task;

		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document);

		var text = await document.GetTextAsync(cancellationToken);
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel);

		var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
		diagnostics = diagnostics.Where(SharpIdeDiagnostic.ShouldShowInEditor).ToImmutableArray();
		var result = diagnostics
			.Select(d =>
			{
				var mappedFileLinePositionSpan = semanticModel.SyntaxTree.GetMappedLineSpan(d.Location.SourceSpan);
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return new SharpIdeDocumentDiagnostics(result, text);
	}

	/// Analyzer diagnostics - from the project's analyzers, and the solution level analyzers (the IDE code style analyzers, e.g. IDE0028). Compiler diagnostics are excluded, see GetDocumentDiagnostics.
	/// This gives the same results as IDiagnosticAnalyzerService (which the Roslyn LSP uses), but is ~40% faster, as IDiagnosticAnalyzerService always runs analyzers non-concurrently.
	public async Task<SharpIdeDocumentDiagnostics> GetDocumentAnalyzerDiagnostics(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return SharpIdeDocumentDiagnostics.Empty;
		if (fileModel.IsMetadataAsSourceFile) return SharpIdeDocumentDiagnostics.Empty; // Decompiled/SourceLink files should not/will not have analyzers
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentAnalyzerDiagnostics)}");
		await _solutionLoadedTcs.Task;

		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document);
		var text = await document.GetTextAsync(cancellationToken);
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel);

		// Cached per Compilation: CompilationWithAnalyzers clones the compilation (losing its binding caches) and remembers which analyzers have already run on which trees.
		// Reusing it means repeated requests against an unchanged compilation return stored results instead of re-running every analyzer from scratch.
		var project = document.Project;
		var compilationWithAnalyzers = _compilationWithAnalyzersCache.GetValue(semanticModel.Compilation, compilation => CreateCompilationWithAnalyzers(compilation, project));

		var tree = semanticModel.SyntaxTree;
		var syntaxResult = await compilationWithAnalyzers.GetAnalysisResultAsync(tree, cancellationToken);
		var semanticResult = await compilationWithAnalyzers.GetAnalysisResultAsync(semanticModel, filterSpan: null, cancellationToken);
		// Only take each pass's own kind of diagnostics for this tree, as Roslyn's DocumentAnalysisExecutor does.
		// GetAllDiagnostics would also include everything already computed on this CompilationWithAnalyzers, so the two passes would duplicate each other.
		var result = GetTreeDiagnostics(syntaxResult.SyntaxDiagnostics, tree)
			.Concat(GetTreeDiagnostics(semanticResult.SemanticDiagnostics, tree))
			.Where(SharpIdeDiagnostic.ShouldShowInEditor)
			.Select(d =>
			{
				var mappedFileLinePositionSpan = semanticModel.SyntaxTree.GetMappedLineSpan(d.Location.SourceSpan);
				return new SharpIdeDiagnostic(mappedFileLinePositionSpan.Span, d, mappedFileLinePositionSpan.Path);
			})
			.ToImmutableArray();
		return new SharpIdeDocumentDiagnostics(result, text);

		static IEnumerable<Diagnostic> GetTreeDiagnostics(ImmutableDictionary<SyntaxTree, ImmutableDictionary<DiagnosticAnalyzer, ImmutableArray<Diagnostic>>> diagnosticsByTree, SyntaxTree tree)
			=> diagnosticsByTree.TryGetValue(tree, out var diagnosticsByAnalyzer) ? diagnosticsByAnalyzer.Values.SelectMany(d => d) : [];
	}

	private static readonly ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _compilationWithAnalyzersCache = new();

	/// Mirrors IDiagnosticAnalyzerService's CreateCompilationWithAnalyzersAsync, except that analyzers run concurrently.
	/// The options matter: project analyzers get the project's options, including AdditionalFiles (e.g. BannedSymbols.txt for the banned API analyzer) and .editorconfig.
	/// Host (solution level) analyzers get the host options, which are .editorconfig, falling back to the IDE's options.
	private static CompilationWithAnalyzers CreateCompilationWithAnalyzers(Compilation compilation, Project project)
	{
		var projectAnalyzers = project.AnalyzerReferences
			.OfType<IsolatedAnalyzerFileReference>()
			.SelectMany(r => r.GetAnalyzers(project.Language))
			.ToHashSet();
		// The compiler analyzer is excluded, as compiler diagnostics come from GetDocumentDiagnostics
		var hostAnalyzers = SolutionLevelAnalyzerReferences.References
			.SelectMany(r => r.GetAnalyzers(project.Language))
			.Where(a => a.IsCompilerAnalyzer() is false);
		var analyzers = projectAnalyzers.Concat(hostAnalyzers).ToImmutableArray();

		var hostAnalyzerOptions = project.State.HostAnalyzerOptions;
		var projectAnalyzerOptions = project.State.ProjectAnalyzerOptions;
		return compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
			options: hostAnalyzerOptions,
			onAnalyzerException: null,
			concurrentAnalysis: true,
			logAnalyzerExecutionTime: false,
			reportSuppressedDiagnostics: false,
			analyzerExceptionFilter: null,
			getAnalyzerConfigOptionsProvider: analyzer => projectAnalyzers.Contains(analyzer)
				? projectAnalyzerOptions.AnalyzerConfigOptionsProvider
				: hostAnalyzerOptions.AnalyzerConfigOptionsProvider));
	}

	/// Returns a version that changes whenever anything that could affect this file's highlighting or diagnostics changes:
	/// any document or additional (e.g. razor) document in its project, the project itself, or any project it references.
	/// Returns null if the file is not part of a loaded workspace project - callers should then assume it has changed.
	public async Task<VersionStamp?> GetFileAnalysisVersion(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return null;
		await _solutionLoadedTcs.Task;
		var project = TryGetTextDocumentForSharpIdeFile(fileModel)?.Project;
		if (project is null) return null;
		// Deliberately not GetDependentSemanticVersionAsync - that only tracks top-level (signature) changes, so it would not change for method body edits
		return await project.GetDependentVersionAsync(cancellationToken);
	}
}
