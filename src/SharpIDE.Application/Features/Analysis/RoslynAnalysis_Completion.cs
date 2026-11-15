using System.Collections.Immutable;
using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.SignatureHelp;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.SolutionDiscovery;
using CompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using CompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using CompletionOptions = Microsoft.CodeAnalysis.Completion.CompletionOptions;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
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

		var document = GetRequiredCSharpDocumentForSharpIdeFile(fileModel);
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
		Guard.Against.Null(document);
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
			Items = signatureHelpItems,
			ApplicableSpan = mappedLinePositionSpan
		};

		return sharpIdeSignatureHelpItems;

		// var symbols = semanticModel.LookupSymbols(position, methodSymbol.ReceiverType, methodSymbol.Name, true)
		// 	.OfType<IMethodSymbol>()
		// 	.ToImmutableArray();
		// return symbols;
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
}
