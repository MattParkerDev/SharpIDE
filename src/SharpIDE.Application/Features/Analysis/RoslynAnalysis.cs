using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Remote.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Text;
using NuGet.Packaging;
using SharpIDE.Application.Features.Analysis.FixLoaders;
using SharpIDE.Application.Features.Analysis.Razor;
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
	public static MSBuildWorkspace? _workspace;
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
	public static async Task Analyse(SharpIdeSolutionModel solutionModel)
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
			_workspace = MSBuildWorkspace.Create(host);
			_workspace.RegisterWorkspaceFailedHandler(o => throw new InvalidOperationException($"Workspace failed: {o.Diagnostic.Message}"));

			var snapshotManager = container.GetExports<RemoteSnapshotManager>().FirstOrDefault();
			_snapshotManager = snapshotManager;

			_semanticTokensLegendService = container.GetExports<RemoteSemanticTokensLegendService>().FirstOrDefault();
			_semanticTokensLegendService!.SetLegend(TokenTypeProvider.ConstructTokenTypes(false), TokenTypeProvider.ConstructTokenModifiers());
		}
		using (var ___ = SharpIdeOtel.Source.StartActivity("OpenSolution"))
		{
			var solution = await _workspace.OpenSolutionAsync(_sharpIdeSolutionModel.FilePath, new Progress());
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

	public static async Task UpdateSolutionDiagnostics()
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(UpdateSolutionDiagnostics)}");
		await _solutionLoadedTcs.Task;
		foreach (var project in _sharpIdeSolutionModel!.AllProjects)
		{
			var projectDiagnostics = await GetProjectDiagnostics(project);
			// TODO: only add and remove diffs
			project.Diagnostics.RemoveRange(project.Diagnostics);
			project.Diagnostics.AddRange(projectDiagnostics);
		}
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

	public static async Task<ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)>> GetDocumentDiagnostics(SharpIdeFile fileModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentDiagnostics)}");
		await _solutionLoadedTcs.Task;
		var cancellationToken = CancellationToken.None;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		var document = project.Documents.SingleOrDefault(s => s.FilePath == fileModel.Path);

		if (document is null) return [];
		//var document = _workspace!.CurrentSolution.GetDocument(fileModel.Path);
		Guard.Against.Null(document, nameof(document));

		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		var diagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
		diagnostics = diagnostics.Where(d => d.Severity is not DiagnosticSeverity.Hidden).ToImmutableArray();
		var result = diagnostics.Select(d => (semanticModel.SyntaxTree.GetMappedLineSpan(d.Location.SourceSpan), d)).ToImmutableArray();
		return result;
	}

	public record SharpIdeRazorMappedClassifiedSpan(SharpIdeRazorSourceSpan SourceSpanInRazor, string CsharpClassificationType);
	public static async Task<IEnumerable<SharpIdeRazorClassifiedSpan>> GetRazorDocumentSyntaxHighlighting(SharpIdeFile fileModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetRazorDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		var cancellationToken = CancellationToken.None;
		var timer = Stopwatch.StartNew();
		var sharpIdeProjectModel = ((IChildSharpIdeNode) fileModel).GetNearestProjectNode()!;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == sharpIdeProjectModel!.FilePath);
		if (!fileModel.Name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
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

	public static async Task<IEnumerable<(FileLinePositionSpan fileSpan, ClassifiedSpan classifiedSpan)>> GetDocumentSyntaxHighlighting(SharpIdeFile fileModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetDocumentSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		var cancellationToken = CancellationToken.None;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		if (fileModel.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) is false)
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

	public static async Task<ImmutableArray<CodeAction>> GetCodeFixesForDocumentAtPosition(SharpIdeFile fileModel, LinePosition linePosition)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeFixesForDocumentAtPosition)}");
		var cancellationToken = CancellationToken.None;
		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
		Guard.Against.Null(document, nameof(document));
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));

		var diagnostics = semanticModel.GetDiagnostics();
		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.Lines.GetPosition(linePosition);
		var diagnosticsAtPosition = diagnostics
			.Where(d => d.Location.IsInSource && d.Location.SourceSpan.Contains(position))
			.ToImmutableArray();

		ImmutableArray<CodeAction> codeActions = [];
		foreach (var diagnostic in diagnosticsAtPosition)
		{
			var actions = await GetCodeFixesAsync(document, diagnostic);
			codeActions = codeActions.AddRange(actions);
		}

		var linePositionSpan = new LinePositionSpan(linePosition, new LinePosition(linePosition.Line, linePosition.Character + 1));
		var selectedSpan = sourceText.Lines.GetTextSpan(linePositionSpan);
		codeActions = codeActions.AddRange(await GetCodeRefactoringsAsync(document, selectedSpan));
		return codeActions;
	}

	public static async Task<ImmutableArray<(FileLinePositionSpan fileSpan, CodeAction codeAction)>> GetCodeFixesAsync(Diagnostic diagnostic)
	{
		var cancellationToken = CancellationToken.None;
		var document = _workspace!.CurrentSolution.GetDocument(diagnostic.Location.SourceTree);
		Guard.Against.Null(document, nameof(document));
		var codeActions = await GetCodeFixesAsync(document, diagnostic);
		var result = codeActions.Select(action => (diagnostic.Location.SourceTree!.GetMappedLineSpan(diagnostic.Location.SourceSpan), action))
			.ToImmutableArray();
		return result;
	}
	private static async Task<ImmutableArray<CodeAction>> GetCodeFixesAsync(Document document, Diagnostic diagnostic)
	{
		var cancellationToken = CancellationToken.None;
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

	private static async Task<ImmutableArray<CodeAction>> GetCodeRefactoringsAsync(Document document, TextSpan span)
	{
		var cancellationToken = CancellationToken.None;
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

	private static async Task<CompletionList> GetCompletionsAsync(Document document, LinePosition linePosition)
	{
		var cancellationToken = CancellationToken.None;
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

	public static async Task ApplyCodeActionAsync(CodeAction codeAction)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ApplyCodeActionAsync)}");
		var cancellationToken = CancellationToken.None;
		var operations = await codeAction.GetOperationsAsync(cancellationToken);
		foreach (var operation in operations)
		{
			operation.Apply(_workspace!, cancellationToken);
			// if (operation is ApplyChangesOperation applyChangesOperation)
			// {
			// 	var newSolution = applyChangesOperation.ChangedSolution;
			// 	_workspace.TryApplyChanges(newSolution);
			// }
			// else
			// {
			// 	throw new NotSupportedException($"Unsupported operation type: {operation.GetType().Name}");
			// }
		}
	}

	// TODO: Use AdhocWorkspace or something else, to avoid writing to disk on every change
	public static void UpdateDocument(SharpIdeFile fileModel, string newContent)
	{
		Guard.Against.Null(fileModel, nameof(fileModel));
		Guard.Against.NullOrEmpty(newContent, nameof(newContent));

		var project = _workspace!.CurrentSolution.Projects.Single(s => s.FilePath == ((IChildSharpIdeNode)fileModel).GetNearestProjectNode()!.FilePath);
		if (fileModel.IsRazorFile)
		{
			var razorDocument = project.AdditionalDocuments.Single(s => s.FilePath == fileModel.Path);
			var newSolution = _workspace.CurrentSolution.WithAdditionalDocumentText(razorDocument.Id, SourceText.From(newContent));
			_workspace.TryApplyChanges(newSolution);
		}
		else
		{
			var document = project.Documents.Single(s => s.FilePath == fileModel.Path);
			Guard.Against.Null(document, nameof(document));
			var newSolution = _workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(newContent));
			_workspace.TryApplyChanges(newSolution);
		}
	}
}
