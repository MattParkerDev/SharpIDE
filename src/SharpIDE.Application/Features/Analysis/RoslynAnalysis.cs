using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Remote.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis.FixLoaders;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.RazorAccess;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using CompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace SharpIDE.Application.Features.Analysis;

public static class RoslynAnalysis
{
	public static AdhocWorkspace? _workspace;
	private static CustomMsBuildProjectLoader? _msBuildProjectLoader;
	private static RemoteSnapshotManager? _snapshotManager;
	private static RemoteSemanticTokensLegendService? _semanticTokensLegendService;
	private static SharpIdeSolutionModel? _sharpIdeSolutionModel;
	private static HashSet<CodeFixProvider> _codeFixProviders = [];
	private static HashSet<CodeRefactoringProvider> _codeRefactoringProviders = [];
	private static TaskCompletionSource _solutionLoadedTcs = null!;
	public static void StartSolutionAnalysis(SharpIdeSolutionModel solutionModel)
	{
		_solutionLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = Task.Run(async () =>
		{
			try
			{
				await Analyse(solutionModel);
			}
			catch (Exception e)
			{
				Console.WriteLine($"RoslynAnalysis: Error during analysis: {e}");
			}
		});
	}
	public static async Task Analyse(SharpIdeSolutionModel solutionModel, CancellationToken cancellationToken = default)
	{
		Console.WriteLine($"RoslynAnalysis: Loading solution");
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(Analyse)}");
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
			_workspace.RegisterWorkspaceFailedHandler(o => Console.WriteLine($"Workspace failed: {o.Diagnostic.Message}"));

			var snapshotManager = container.GetExports<RemoteSnapshotManager>().FirstOrDefault();
			_snapshotManager = snapshotManager;

			_semanticTokensLegendService = container.GetExports<RemoteSemanticTokensLegendService>().FirstOrDefault();
			_semanticTokensLegendService!.SetLegend(TokenTypeProvider.ConstructTokenTypes(false), TokenTypeProvider.ConstructTokenModifiers());

			_msBuildProjectLoader = new CustomMsBuildProjectLoader(_workspace);
		}
		using (var ___ = SharpIdeOtel.Source.StartActivity("OpenSolution"))
		{
			//_msBuildProjectLoader!.LoadMetadataForReferencedProjects = true;

			// MsBuildProjectLoader doesn't do a restore which is absolutely required for resolving PackageReferences, if they have changed. I am guessing it just reads from project.assets.json
			await BuildService.Instance.MsBuildAsync(_sharpIdeSolutionModel.FilePath, BuildType.Restore, cancellationToken);
			var solutionInfo = await _msBuildProjectLoader!.LoadSolutionInfoAsync(_sharpIdeSolutionModel.FilePath, cancellationToken: cancellationToken);
			_workspace.ClearSolution();
			var solution = _workspace.AddSolution(solutionInfo);
		}
		timer.Stop();
		Console.WriteLine($"RoslynAnalysis: Solution loaded in {timer.ElapsedMilliseconds}ms");
		_solutionLoadedTcs.SetResult();

		using (var ____ = SharpIdeOtel.Source.StartActivity("LoadAnalyzersAndFixers"))
		{
			foreach (var assembly in MefHostServices.DefaultAssemblies)
			{
				var fixers = CodeFixProviderLoader.LoadCodeFixProviders([assembly], LanguageNames.CSharp);
				_codeFixProviders.AddRange(fixers);
				var refactoringProviders = CodeRefactoringProviderLoader.LoadCodeRefactoringProviders([assembly], LanguageNames.CSharp);
				_codeRefactoringProviders.AddRange(refactoringProviders);
			}
			_codeFixProviders = _codeFixProviders.DistinctBy(s => s.GetType().Name).ToHashSet();
			_codeRefactoringProviders = _codeRefactoringProviders.DistinctBy(s => s.GetType().Name).ToHashSet();
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

		await UpdateSolutionDiagnostics();
		// foreach (var project in solution.Projects)
		// {
		// 	// foreach (var document in project.Documents)
		// 	// {
		// 	// 	var semanticModel = await document.GetSemanticModelAsync();
		// 	// 	Guard.Against.Null(semanticModel, nameof(semanticModel));
		// 	// 	var documentDiagnostics = semanticModel.GetDiagnostics().Where(d => d.Severity is not DiagnosticSeverity.Hidden).ToList();
		// 	// 	foreach (var diagnostic in documentDiagnostics)
		// 	// 	{
		// 	// 		var test = await GetCodeFixesAsync(document, diagnostic);
		// 	// 	}
		// 	// 	// var syntaxTree = await document.GetSyntaxTreeAsync();
		// 	// 	// var root = await syntaxTree!.GetRootAsync();
		// 	// 	// var classifiedSpans = await Classifier.GetClassifiedSpansAsync(document, root.FullSpan);
		// 	// 	// foreach (var span in classifiedSpans)
		// 	// 	// {
		// 	// 	// 	var classifiedSpan = root.GetText().GetSubText(span.TextSpan);
		// 	// 	// 	Console.WriteLine($"{span.TextSpan}: {span.ClassificationType}");
		// 	// 	// 	Console.WriteLine(classifiedSpan);
		// 	// 	// }
		// 	// }
		// }
		Console.WriteLine("RoslynAnalysis: Analysis completed.");
	}

	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public static async Task ReloadSolution(CancellationToken cancellationToken = default)
	{
		Console.WriteLine($"RoslynAnalysis: Reloading Solution");
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadSolution)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(_workspace, nameof(_workspace));
		Guard.Against.Null(_msBuildProjectLoader, nameof(_msBuildProjectLoader));

		// It is important to note that a Workspace has no concept of MSBuild, nuget packages etc. It is just told about project references and "metadata" references, which are dlls. This is the what MSBuild does - it reads the csproj, and most importantly resolves nuget package references to dlls
		await BuildService.Instance.MsBuildAsync(_sharpIdeSolutionModel!.FilePath, BuildType.Restore, cancellationToken);
		var __ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.MSBuildProjectLoader.LoadSolutionInfoAsync");
		// This call is the expensive part - MSBuild is slow. There doesn't seem to be any incrementalism for solutions.
		// The best we could do to speed it up is do .LoadProjectInfoAsync for the single project, and somehow munge that into the existing solution
		var newSolutionInfo = await _msBuildProjectLoader.LoadSolutionInfoAsync(_sharpIdeSolutionModel!.FilePath, cancellationToken: cancellationToken);
		__?.Dispose();

		var ___ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.Workspace.OnSolutionReloaded");
		// There doesn't appear to be any noticeable difference between ClearSolution + AddSolution vs OnSolutionReloaded
		//_workspace.OnSolutionReloaded(newSolutionInfo);
		_workspace.ClearSolution();
		_workspace.AddSolution(newSolutionInfo);
		___?.Dispose();
		Console.WriteLine("RoslynAnalysis: Solution reloaded");
	}

	/// Callers should call UpdateSolutionDiagnostics after this
	/// Ensure that the SharpIdeSolutionModel has been updated before calling this and any subsequent calls
	public static async Task ReloadProject(SharpIdeProjectModel projectModel, CancellationToken cancellationToken = default)
	{
		Console.WriteLine($"RoslynAnalysis: Reloading Project {projectModel.FilePath}");
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ReloadSolution)}");
		await _solutionLoadedTcs.Task;
		Guard.Against.Null(_workspace, nameof(_workspace));
		Guard.Against.Null(_msBuildProjectLoader, nameof(_msBuildProjectLoader));

		await BuildService.Instance.MsBuildAsync(_sharpIdeSolutionModel!.FilePath, BuildType.Restore, cancellationToken);
		var __ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(MSBuildProjectLoader)}.{nameof(MSBuildProjectLoader.LoadProjectInfoAsync)}");

		var thisProject = _workspace.CurrentSolution.Projects.Single(s => s.FilePath == projectModel.FilePath);

		// we can reliably rely on the Solution's graph of project inter-references, as a project has only been reloaded - no projects have been added or removed from the solution
		var dependentProjects = _workspace.CurrentSolution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(thisProject.Id);
		var projectPathsToReload = dependentProjects.Select(id => _workspace.CurrentSolution.GetProject(id)!.FilePath!).Append(thisProject.FilePath!).Distinct().ToList();
		//var projectMap = ProjectMap.Create(_workspace.CurrentSolution); // using a projectMap may speed up LoadProjectInfosAsync, TODO: test
		// This will get all projects necessary to build this group of projects, regardless of whether those projects are actually affected by the original project change
		// We can potentially optimise this, but given this is the expensive part, lets just proceed with reloading them all in the solution
		// We potentially lose performance because Workspace/Solution caches are dropped, but lets not prematurely optimise
		var loadedProjectInfos = await _msBuildProjectLoader.LoadProjectInfosAsync(projectPathsToReload, null, cancellationToken: cancellationToken);
		__?.Dispose();

		var ___ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.Workspace.asdf");

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
			var existingProject = _workspace.CurrentSolution.Projects.Single(p => p.FilePath == loadedProjectInfo.FilePath);
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
		Console.WriteLine("RoslynAnalysis: Project reloaded");
		return;
		ProjectReference MapProjectReference(ProjectReference oldRef) => new ProjectReference(projectIdMap[oldRef.ProjectId], oldRef.Aliases, oldRef.EmbedInteropTypes);

		static ImmutableArray<DocumentInfo> MapDocuments(Solution oldSolution, ProjectId mappedProjectId, IReadOnlyList<DocumentInfo> documents)
			=> documents.Select(docInfo =>
			{
				var mappedDocumentId = oldSolution.GetDocumentIdsWithFilePath(docInfo.FilePath).Single(id => id.ProjectId == mappedProjectId);
				return docInfo.WithId(mappedDocumentId);
			}).ToImmutableArray();
	}

	public static async Task UpdateSolutionDiagnostics()
	{
		Console.WriteLine("RoslynAnalysis: Updating solution diagnostics");
		var timer = Stopwatch.StartNew();
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(UpdateSolutionDiagnostics)}");
		await _solutionLoadedTcs.Task;
		foreach (var project in _sharpIdeSolutionModel!.AllProjects)
		{
			var projectDiagnostics = await GetProjectDiagnostics(project);
			// TODO: only add and remove diffs
			project.Diagnostics.RemoveRange(project.Diagnostics);
			project.Diagnostics.AddRange(projectDiagnostics);
		}
		timer.Stop();
		Console.WriteLine($"RoslynAnalysis: Solution diagnostics updated in {timer.ElapsedMilliseconds}ms");
	}

	public static async Task<ImmutableArray<Diagnostic>> GetProjectDiagnostics(SharpIdeProjectModel projectModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetProjectDiagnostics)}");
		await _solutionLoadedTcs.Task;
		var cancellationToken = CancellationToken.None;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == projectModel.FilePath);
		var compilation = await project.GetCompilationAsync(cancellationToken);
		Guard.Against.Null(compilation, nameof(compilation));

		var diagnostics = compilation.GetDiagnostics(cancellationToken);
		diagnostics = diagnostics.Where(d => d.Severity is not DiagnosticSeverity.Hidden).ToImmutableArray();
		return diagnostics;
	}

	public static async Task<ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)>> GetDocumentDiagnostics(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		if (fileModel.IsRoslynWorkspaceFile is false) return [];
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentDiagnostics)}");
		await _solutionLoadedTcs.Task;

		var document = await GetDocumentForSharpIdeFile(fileModel);
		Guard.Against.Null(document, nameof(document));

		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
		diagnostics = diagnostics.Where(d => d.Severity is not DiagnosticSeverity.Hidden).ToImmutableArray();
		var result = diagnostics.Select(d => (semanticModel.SyntaxTree.GetMappedLineSpan(d.Location.SourceSpan), d)).ToImmutableArray();
		return result;
	}

	private static async Task<Document> GetDocumentForSharpIdeFile(SharpIdeFile fileModel)
	{
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		var document = fileModel.IsCsharpFile ? project.Documents.SingleOrDefault(s => s.FilePath == fileModel.Path)
				: await GetRazorSourceGeneratedDocumentInProjectForSharpIdeFile(project, fileModel);
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
	public static async Task<IEnumerable<SharpIdeRazorClassifiedSpan>> GetRazorDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetRazorDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		var timer = Stopwatch.StartNew();
		var sharpIdeProjectModel = ((IChildSharpIdeNode) fileModel).GetNearestProjectNode()!;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == sharpIdeProjectModel!.FilePath);
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
		var (razorSpans, sourceMappings) = RazorAccessors.GetSpansAndMappingsForRazorCodeDocument(razorCodeDocument, razorCSharpDocument);
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
		sharpIdeRazorSpans = sharpIdeRazorSpans.OrderBy(s => s.Span.AbsoluteIndex).ToList();
		timer.Stop();
		Console.WriteLine($"RoslynAnalysis: Razor syntax highlighting for {fileModel.Name} took {timer.ElapsedMilliseconds}ms");
		return sharpIdeRazorSpans;
	}

	public static async Task<IEnumerable<(FileLinePositionSpan fileSpan, ClassifiedSpan classifiedSpan)>> GetDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
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

		var result = classifiedSpans.Select(s => (syntaxTree.GetMappedLineSpan(s.TextSpan), s));

		return result;
	}

	public static async Task<CompletionList> GetCodeCompletionsForDocumentAtPosition(SharpIdeFile fileModel, LinePosition linePosition)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeCompletionsForDocumentAtPosition)}");
		await _solutionLoadedTcs.Task;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));
		var completions = await GetCompletionsAsync(document, linePosition).ConfigureAwait(false);
		return completions;
	}

	public static async Task<ImmutableArray<CodeAction>> GetCodeFixesForDocumentAtPosition(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeFixesForDocumentAtPosition)}");
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(fileModel);
		Guard.Against.Null(document, nameof(document));
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken); // TODO: pass span
		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.Lines.GetPosition(linePosition);
		var diagnosticsAtPosition = diagnostics
			.Where(d => d.Location.IsInSource && d.Location.SourceSpan.Contains(position))
			.ToImmutableArray();

		ImmutableArray<CodeAction> codeActions = [];
		foreach (var diagnostic in diagnosticsAtPosition)
		{
			var actions = await GetCodeFixesAsync(document, diagnostic, cancellationToken);
			codeActions = codeActions.AddRange(actions);
		}

		var linePositionSpan = new LinePositionSpan(linePosition, new LinePosition(linePosition.Line, linePosition.Character + 1));
		var selectedSpan = sourceText.Lines.GetTextSpan(linePositionSpan);
		codeActions = codeActions.AddRange(await GetCodeRefactoringsAsync(document, selectedSpan, cancellationToken));
		return codeActions;
	}

	private static async Task<ImmutableArray<CodeAction>> GetCodeFixesAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken = default)
	{
		var codeActions = new List<CodeAction>();
		var context = new CodeFixContext(
			document,
			diagnostic,
			(action, _) => codeActions.Add(action), // callback collects fixes
			cancellationToken
		);

		var relevantProviders = _codeFixProviders
			.Where(provider => provider.FixableDiagnosticIds.Contains(diagnostic.Id));

		foreach (var provider in relevantProviders)
		{
			await provider.RegisterCodeFixesAsync(context);
		}

		return codeActions.ToImmutableArray();
	}

	private static async Task<ImmutableArray<CodeAction>> GetCodeRefactoringsAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
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

	private static async Task<CompletionList> GetCompletionsAsync(Document document, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var completionService = CompletionService.GetService(document);
		if (completionService is null) throw new InvalidOperationException("Completion service is not available for the document.");

		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.Lines.GetPosition(linePosition);
		var completions = await completionService.GetCompletionsAsync(document, position, cancellationToken: cancellationToken);

		// foreach (var item in completions.ItemsList)
		// {
		// 	Console.WriteLine($"Completion: {item.DisplayText}");
		// }
		return completions;
	}

	/// Returns the list of files modified by applying the code action
	public static async Task<List<(SharpIdeFile File, string UpdatedText)>> ApplyCodeActionAsync(CodeAction codeAction)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ApplyCodeActionAsync)}");
		var cancellationToken = CancellationToken.None;
		var operations = await codeAction.GetOperationsAsync(cancellationToken);
		var changedDocumentIds = new List<DocumentId>();
		foreach (var operation in operations)
		{
			if (operation is ApplyChangesOperation applyChangesOperation)
			{
				var newSolution = applyChangesOperation.ChangedSolution;
				var changedDocIds = newSolution
					.GetChanges(_workspace!.CurrentSolution)
					.GetProjectChanges()
					.SelectMany(s => s.GetChangedDocuments());
				changedDocumentIds.AddRange(changedDocIds);

				_workspace.TryApplyChanges(newSolution);
			}
			else
			{
				throw new NotSupportedException($"Unsupported operation type: {operation.GetType().Name}");
			}
		}

		var changedFilesWithText = await changedDocumentIds
			.DistinctBy(s => s.Id)
			.Select(id => _workspace!.CurrentSolution.GetDocument(id))
			.Where(d => d is not null)
			.OfType<Document>() // ensures non-null
			.ToAsyncEnumerable()
			.Select(async (Document doc, CancellationToken ct) =>
			{
				var text = await doc.GetTextAsync(ct);
				var sharpFile = _sharpIdeSolutionModel!.AllFiles.Single(f => f.Path == doc.FilePath);
				return (sharpFile, text.ToString());
			})
			.ToListAsync(cancellationToken);

		return changedFilesWithText;
	}

	public static async Task<(ISymbol?, LinePositionSpan?)> LookupSymbol(SharpIdeFile fileModel, LinePosition linePosition)
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

	private static async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan)> LookupSymbolInRazor(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var sharpIdeProjectModel = ((IChildSharpIdeNode) fileModel).GetNearestProjectNode()!;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == sharpIdeProjectModel!.FilePath);

		var additionalDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(additionalDocument);

		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		var razorCSharpDocument = razorCodeDocument.GetRequiredCSharpDocument();
		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);

		var mappedPosition = MapRazorLinePositionToGeneratedCSharpAbsolutePosition(razorCSharpDocument, razorText, linePosition);
		var semanticModelAsync = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModelAsync!, generatedDocSyntaxRoot!, mappedPosition!.Value);
		return (symbol, linePositionSpan);
	}

	private static async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan)> LookupSymbolInCs(SharpIdeFile fileModel, LinePosition linePosition)
	{
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));
		var sourceText = await document.GetTextAsync();
		var position = sourceText.GetPosition(linePosition);
		var semanticModel = await document.GetSemanticModelAsync();
		Guard.Against.Null(semanticModel, nameof(semanticModel));
		var syntaxRoot = await document.GetSyntaxRootAsync();
		return GetSymbolAtPosition(semanticModel, syntaxRoot!, position);
	}

	private static (ISymbol? symbol, LinePositionSpan? linePositionSpan) GetSymbolAtPosition(SemanticModel semanticModel, SyntaxNode root, int position)
	{
		var node = root.FindToken(position).Parent!;
		var symbol = semanticModel.GetSymbolInfo(node).Symbol ?? semanticModel.GetDeclaredSymbol(node);
		if (symbol is null)
		{
			Console.WriteLine("No symbol found at position");
			return (null, null);
		}

		var linePositionSpan = root.SyntaxTree.GetLineSpan(node.Span).Span;
		Console.WriteLine($"Symbol found: {symbol.Name} ({symbol.Kind}) - {symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
		return (symbol, linePositionSpan);
	}

	private static int? MapRazorLinePositionToGeneratedCSharpAbsolutePosition(RazorCSharpDocument razorCSharpDocument, SourceText razorText, LinePosition razorLinePosition)
	{
		var mappings = razorCSharpDocument.SourceMappings;
		var razorOffset = razorText.Lines.GetPosition(razorLinePosition);

		foreach (var mapping in mappings)
		{
			var span = mapping.OriginalSpan;
			if (razorOffset >= span.AbsoluteIndex && razorOffset < span.AbsoluteIndex + span.Length)
			{
				// Calculate offset within the mapping
				var offsetInMapping = razorOffset - span.AbsoluteIndex;
				// Map to generated C# position
				return mapping.GeneratedSpan.AbsoluteIndex + offsetInMapping;
			}
		}
		return null;
	}

	public static async Task UpdateDocument(SharpIdeFile fileModel, string newContent)
	{
		Guard.Against.Null(fileModel, nameof(fileModel));
		Guard.Against.NullOrEmpty(newContent, nameof(newContent));

		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);

		var sourceText = SourceText.From(newContent);
		var document = fileModel switch
		{
			{ IsRazorFile: true } => project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path),
			{ IsCsharpFile: true } => project.Documents.Single(s => s.FilePath == fileModel.Path),
			_ => throw new InvalidOperationException("UpdateDocument failed: File is not in workspace")
		};

		var oldText = await document.GetTextAsync();

		// Compute minimal text changes
		var changes = sourceText.GetChangeRanges(oldText);
		if (changes.Count == 0)
			return; // No changes, nothing to apply

		var newText = oldText.WithChanges(sourceText.GetTextChanges(oldText));

		var newSolution = fileModel switch
		{
			{ IsRazorFile: true } => _workspace.CurrentSolution.WithAdditionalDocumentText(document.Id, newText),
			{ IsCsharpFile: true } => _workspace.CurrentSolution.WithDocumentText(document.Id, newText),
			_ => throw new ArgumentOutOfRangeException()
		};

		_workspace.TryApplyChanges(newSolution);
	}
}
