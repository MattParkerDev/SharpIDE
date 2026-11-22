using System.Collections.Immutable;
using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeFixesAndRefactorings;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler.CodeActions;
using Microsoft.CodeAnalysis.Suggestions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;
using CodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
	/// Gets the code fixes and refactorings for the selection (an empty span for just the caret), filtered and ordered the same way as the Roslyn LSP (VS Code) - e.g. error fixes first, grouped by diagnostic and priority.
	public async Task<ImmutableArray<IdeCodeAction>> GetCodeActionsForDocumentAtPosition(SharpIdeFile fileModel, LinePositionSpan selection, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeActionsForDocumentAtPosition)}");
		await _solutionLoadedTcs.Task;
		var document = await GetDocumentForSharpIdeFile(fileModel, cancellationToken);
		Guard.Against.Null(document);

		// This mirrors CodeActionHelpers.GetCodeActionsAsync, but keeps each action's kind, which it discards
		// ICodeFixService gets the diagnostics to fix from IDiagnosticAnalyzerService, so the solution level (host) analyzers must be registered for fixes to compiler and IDE diagnostics - see SolutionLevelAnalyzerReferences
		var actionSets = await CodeActionHelpers.GetActionSetsAsync(
			document,
			_codeFixService!,
			_codeRefactoringService!,
			ProtocolConversions.LinePositionToRange(selection),
			cancellationToken);
		if (actionSets.IsDefaultOrEmpty) return [];

		var containerTitles = new Dictionary<(DocumentId, int Position, bool IsMember), string?>();
		var codeActions = ImmutableArray.CreateBuilder<IdeCodeAction>();
		foreach (var set in actionSets)
		{
			foreach (var suggestedAction in set.Actions)
			{
				if (CodeActionHelpers.IsCodeActionNotSupportedByLSP(suggestedAction)) continue;
				var lspKind = CodeActionHelpers.GetCodeActionKindFromSuggestedActionCategoryName(set.CategoryName!, suggestedAction);
				var isErrorFix = set.CategoryName is UnifiedPredefinedSuggestedActionCategoryNames.ErrorFix;
				codeActions.Add(new IdeCodeAction(await ToItem(suggestedAction), ToIdeCodeActionKind(lspKind), isErrorFix));
			}
		}
		return codeActions.ToImmutable();

		// Mirrors CodeActionHelpers.GetNestedActionsFromActionSet, but rather than adding a "Fix All" action alongside each action (for one scope), nests every scope under the action
		async Task<IdeCodeActionItem> ToItem(SuggestedAction suggestedAction)
		{
			var codeAction = suggestedAction.CodeAction;
			if (suggestedAction.NestedActionSets.IsEmpty is false)
			{
				var children = ImmutableArray.CreateBuilder<IdeCodeActionItem>();
				foreach (var nestedAction in suggestedAction.NestedActionSets.SelectMany(s => s.Actions))
				{
					children.Add(await ToItem(nestedAction));
				}
				return new IdeCodeActionItem(codeAction.Title, null, children.ToImmutable());
			}
			return new IdeCodeActionItem(codeAction.Title, codeAction, await GetFixAllItems(suggestedAction));
		}

		// Cheap - Roslyn has already computed the fix all states, nothing is fixed until one is applied
		async Task<ImmutableArray<IdeCodeActionItem>> GetFixAllItems(SuggestedAction suggestedAction)
		{
			if (suggestedAction.Flavors is not { Actions.IsDefaultOrEmpty: false } flavors) return [];
			var items = ImmutableArray.CreateBuilder<IdeCodeActionItem>();
			foreach (var state in flavors.Actions.Select(a => a.RefactorOrFixAllState).OfType<IRefactorOrFixAllState>().OrderBy(s => GetScopeOrder(s.Scope)))
			{
				var title = await GetScopeTitle(state);
				items.Add(new IdeCodeActionItem(title, new RefactorOrFixAllCodeAction(state, showPreviewChangesDialog: false, title: $"{suggestedAction.CodeAction.Title} {title}"), []));
			}
			return items.ToImmutable();
		}

		async Task<string> GetScopeTitle(IRefactorOrFixAllState state) => state.Scope switch
		{
			FixAllScope.ContainingMember => $"in {await GetContainerTitle(state, isMember: true) ?? "member"}",
			FixAllScope.ContainingType => $"in {await GetContainerTitle(state, isMember: false) ?? "type"}",
			FixAllScope.Document => "in Document",
			FixAllScope.Project => "in Project",
			FixAllScope.Solution => "in Solution",
			_ => $"in {state.Scope}"
		};

		// Found the same way Roslyn finds the spans to fix (AbstractFixAllSpanMappingService)
		async Task<string?> GetContainerTitle(IRefactorOrFixAllState state, bool isMember)
		{
			if (state.Document is not { } containerDocument) return null;
			var position = state switch
			{
				FixAllState { DiagnosticSpan: { } diagnosticSpan } => diagnosticSpan.Start,
				RefactorAllState refactorAllState => refactorAllState._selectionSpan.Start,
				_ => (await containerDocument.GetTextAsync(cancellationToken)).Lines.GetPosition(selection.Start)
			};
			var key = (containerDocument.Id, position, isMember);
			if (containerTitles.TryGetValue(key, out var cachedTitle)) return cachedTitle;

			var root = await containerDocument.GetRequiredSyntaxRootAsync(cancellationToken);
			var syntaxFacts = containerDocument.GetRequiredLanguageService<ISyntaxFactsService>();
			var declaration = isMember ? syntaxFacts.GetContainingMemberDeclaration(root, position) : syntaxFacts.GetContainingTypeDeclaration(root, position);
			string? title = null;
			if (declaration is not null)
			{
				var semanticModel = await containerDocument.GetRequiredSemanticModelAsync(cancellationToken);
				title = DescribeSymbol(semanticModel.GetDeclaredSymbol(declaration, cancellationToken));
			}
			containerTitles[key] = title;
			return title;
		}

		static string? DescribeSymbol(ISymbol? symbol) => symbol switch
		{
			IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } method => $"constructor '{method.ContainingType.Name}'",
			IMethodSymbol { MethodKind: MethodKind.Destructor } method => $"finalizer '{method.ContainingType.Name}'",
			IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
			IMethodSymbol { AssociatedSymbol: { } associatedSymbol } => DescribeSymbol(associatedSymbol),
			IMethodSymbol method => $"method '{method.Name}'",
			IPropertySymbol { IsIndexer: true } => "indexer",
			IPropertySymbol property => $"property '{property.Name}'",
			IEventSymbol @event => $"event '{@event.Name}'",
			IFieldSymbol field => $"field '{field.Name}'",
			INamedTypeSymbol type => $"{GetTypeKindName(type)} '{type.Name}'",
			_ => null
		};

		static string GetTypeKindName(INamedTypeSymbol type) => type switch
		{
			{ IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
			{ IsRecord: true } => "record",
			{ TypeKind: TypeKind.Class } => "class",
			{ TypeKind: TypeKind.Struct } => "struct",
			{ TypeKind: TypeKind.Interface } => "interface",
			{ TypeKind: TypeKind.Enum } => "enum",
			{ TypeKind: TypeKind.Delegate } => "delegate",
			_ => "type"
		};

		static int GetScopeOrder(FixAllScope scope) => scope switch
		{
			FixAllScope.ContainingMember => 0,
			FixAllScope.ContainingType => 1,
			FixAllScope.Document => 2,
			FixAllScope.Project => 3,
			FixAllScope.Solution => 4,
			_ => 5
		};

		static IdeCodeActionKind ToIdeCodeActionKind(Roslyn.LanguageServer.Protocol.CodeActionKind kind)
		{
			if (kind == Roslyn.LanguageServer.Protocol.CodeActionKind.QuickFix) return IdeCodeActionKind.QuickFix;
			if (kind == Roslyn.LanguageServer.Protocol.CodeActionKind.RefactorExtract) return IdeCodeActionKind.RefactorExtract;
			if (kind == Roslyn.LanguageServer.Protocol.CodeActionKind.RefactorInline) return IdeCodeActionKind.RefactorInline;
			return IdeCodeActionKind.Refactor;
		}
	}

	public static bool IsFixAllCodeAction(CodeAction codeAction) => codeAction is RefactorOrFixAllCodeAction;

	/// Returns the file changes that applying the code action would make, and where to navigate afterwards. Does not apply the changes to the workspace sln
	public async Task<CodeActionApplyChanges> GetCodeActionApplyChanges(CodeAction codeAction, IProgress<CodeAnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetCodeActionApplyChanges)}");
		await _solutionLoadedTcs.Task;
		var originalSolution = _workspace!.CurrentSolution;
		var operations = await codeAction.GetOperationsAsync(originalSolution, progress ?? CodeAnalysisProgress.None, cancellationToken);
		var updatedSolution = originalSolution;
		(DocumentId DocumentId, int Position)? navigationTarget = null;
		foreach (var operation in operations)
		{
			switch (operation)
			{
				case ApplyChangesOperation applyChangesOperation:
					{
						var changes = applyChangesOperation.ChangedSolution.GetChanges(updatedSolution);
						// Linked files are e.g. files with the same path, but different projects, ie different TFMs
						updatedSolution = await applyChangesOperation.ChangedSolution.WithMergedLinkedFileChangesAsync(updatedSolution, changes, cancellationToken).ConfigureAwait(false);
						break;
					}
				// e.g. generate method/constructor places the caret in the generated member
				case DocumentNavigationOperation navigationOperation:
					navigationTarget = (navigationOperation.DocumentId, navigationOperation.Position);
					break;
				// e.g. introduce local/parameter - we have no inline rename session, so just put the caret on the new name
				case StartInlineRenameSessionOperation renameOperation:
					navigationTarget = (renameOperation.DocumentId, renameOperation.Position);
					break;
				// e.g. move type to file opens the new file
				case OpenDocumentOperation openDocumentOperation:
					navigationTarget ??= (openDocumentOperation.DocumentId, 0);
					break;
				default:
					// As Roslyn's LSP does - 3rd party fixers may return operations that do more than change documents (e.g. install a package), apply only the document changes
					_logger.LogWarning("Skipping code action operation '{OperationType}' for '{CodeActionTitle}'", operation.GetType().FullName, codeAction.Title);
					break;
			}
		}

		var fileChanges = await GetSolutionFileChanges(originalSolution, updatedSolution, cancellationToken);
		var navigation = navigationTarget is var (documentId, position) ? await GetNavigation(documentId, position) : null;
		return new CodeActionApplyChanges(fileChanges, navigation);

		async Task<SolutionFileNavigation?> GetNavigation(DocumentId documentId, int position)
		{
			if (await updatedSolution.GetTextDocumentAsync(documentId, cancellationToken) is not { } document) return null;
			var oldDocument = await originalSolution.GetTextDocumentAsync(documentId, cancellationToken);
			if (GetFilePath(document, oldDocument) is not { } filePath) return null;
			var text = await document.GetTextAsync(cancellationToken);
			return new SolutionFileNavigation(filePath, text.Lines.GetLinePosition(Math.Clamp(position, 0, text.Length)));
		}
	}

	/// The file changes that turn oldSolution into newSolution - mirrors Roslyn's LSP CodeActionResolveHelper, across documents, additional documents (e.g. txt files) and analyzer config documents (.editorconfig).
	/// Changes to project/metadata/analyzer references are not file changes, so aren't included - code actions that make them are filtered out (CodeAction.RequiresNonDocumentChange).
	private async Task<SolutionFileChanges> GetSolutionFileChanges(Solution oldSolution, Solution newSolution, CancellationToken cancellationToken = default)
	{
		var result = new SolutionFileChanges();
		var solutionChanges = newSolution.GetChanges(oldSolution);
		var projectChanges = solutionChanges.GetProjectChanges().ToList();
		// Linked documents (e.g. one per TFM) share a file, so each file is only changed/added/removed once
		var seenFilePaths = new HashSet<string>(PathComparison.Comparer);

		foreach (var projectChange in projectChanges)
		{
			if (projectChange.GetAddedProjectReferences().Any() || projectChange.GetRemovedProjectReferences().Any() ||
				projectChange.GetAddedMetadataReferences().Any() || projectChange.GetRemovedMetadataReferences().Any() ||
				projectChange.GetAddedAnalyzerReferences().Any() || projectChange.GetRemovedAnalyzerReferences().Any())
			{
				_logger.LogWarning("Ignoring reference changes to project '{ProjectName}' - only file changes are supported", projectChange.NewProject.Name);
			}
		}

		// Removed
		foreach (var documentId in projectChanges.SelectMany(pc => pc.GetRemovedDocuments().Concat(pc.GetRemovedAdditionalDocuments()).Concat(pc.GetRemovedAnalyzerConfigDocuments())))
		{
			if ((await oldSolution.GetTextDocumentAsync(documentId, cancellationToken))?.FilePath is not { } filePath) continue;
			// A file removed from one project, but still in another (e.g. linked), stays on disk
			if (newSolution.GetDocumentIdsWithFilePath(filePath).IsEmpty is false) continue;
			if (seenFilePaths.Add(filePath)) result.RemovedFilePaths.Add(filePath);
		}

		// Added
		foreach (var documentId in projectChanges.SelectMany(pc => pc.GetAddedDocuments().Concat(pc.GetAddedAdditionalDocuments()).Concat(pc.GetAddedAnalyzerConfigDocuments())))
		{
			if (await newSolution.GetTextDocumentAsync(documentId, cancellationToken) is not { } document) continue;
			if (GetFilePath(document, oldDocument: null) is not { } filePath)
			{
				_logger.LogWarning("Can't determine a file path for added document '{DocumentName}' - skipping it", document.Name);
				continue;
			}
			// e.g. a file added to several projects, or already in another project - it is created once, and project membership follows from the project system
			if (oldSolution.GetDocumentIdsWithFilePath(filePath).IsEmpty is false || seenFilePaths.Add(filePath) is false) continue;
			result.AddedFiles.Add(new FileAddition(filePath, await document.GetTextAsync(cancellationToken)));
		}

		// Changed - text and/or name (e.g. "Rename file to match type")
		var changedDocumentIds = projectChanges.SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetChangedAdditionalDocuments()).Concat(pc.GetChangedAnalyzerConfigDocuments()));
		foreach (var documentId in changedDocumentIds)
		{
			var oldDocument = await oldSolution.GetTextDocumentAsync(documentId, cancellationToken);
			var newDocument = await newSolution.GetTextDocumentAsync(documentId, cancellationToken);
			if (oldDocument?.FilePath is not { } filePath || newDocument is null) continue;
			if (seenFilePaths.Add(filePath) is false) continue;

			var oldText = await oldDocument.GetTextAsync(cancellationToken);
			// For documents, Roslyn can diff the syntax trees, giving precise changes. Otherwise we would get one change spanning the whole file.
			var changes = (oldDocument, newDocument) is (Document oldDoc, Document newDoc)
				? (await newDoc.GetTextChangesAsync(oldDoc, cancellationToken)).ToImmutableArray()
				: (await newDocument.GetTextAsync(cancellationToken)).GetTextChanges(oldText).ToImmutableArray();
			// Text first, at the old path, then the rename - as LSP clients apply them
			if (changes.IsEmpty is false) result.ChangedFiles.Add(new FileTextChange(filePath, oldText, changes));
			if (GetFilePath(newDocument, oldDocument) is { } newFilePath && PathComparison.PathEquals(newFilePath, filePath) is false)
			{
				result.RenamedFiles.Add(new FileRename(filePath, newFilePath));
			}
		}

		if (solutionChanges.GetExplicitlyChangedSourceGeneratedDocuments().Any())
		{
			_logger.LogWarning("Ignoring changes to source generated documents - they have no file to change");
		}
		return result;
	}

	/// The path of a document in a changed solution. A renamed document (e.g. "Rename file to match type") changes its name, but not necessarily its path, as in Roslyn's LSP (GetUriForRenamedDocument).
	/// A new document may not have a path, so it goes in its project's folder, as in Roslyn's LSP (CreateUriForDocumentWithoutFilePath).
	private static string? GetFilePath(TextDocument document, TextDocument? oldDocument)
	{
		if (document is SourceGeneratedDocument) return null;
		if (oldDocument?.FilePath is { } oldFilePath)
		{
			if (document.Name != oldDocument.Name) return Path.Combine(Path.GetDirectoryName(document.FilePath ?? oldFilePath)!, document.Name);
			return document.FilePath ?? oldFilePath;
		}
		if (document.FilePath is not null) return document.FilePath;
		if (document.Project.FilePath is null) return null;
		return Path.Combine([Path.GetDirectoryName(document.Project.FilePath)!, .. document.Folders, document.Name]);
	}
}
