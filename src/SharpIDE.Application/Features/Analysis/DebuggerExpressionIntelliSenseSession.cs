using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

public sealed record DebuggerExpressionAnalysisResult(ImmutableArray<SharpIdeClassifiedSpan> ClassifiedSpans, ImmutableArray<SharpIdeDiagnostic> Diagnostics);
public sealed record DebuggerExpressionCompletionResult(Document Document, CompletionList CompletionList, string ExpressionText, int ExpressionStart);
public sealed record DebuggerExpressionCompletionChange(string Text, LinePosition CaretPosition);

// 🤖
public sealed class DebuggerExpressionIntelliSenseSession : IDisposable
{
	private const string StatementTerminator = ";";

	private readonly DebuggerIntelliSenseWorkspace _workspace;
	private readonly DocumentId _documentId;
	private readonly ImmutableArray<DocumentId> _linkedDocumentIds;
	private readonly SourceText _sourceText;
	private readonly int _insertionPosition;
	private readonly string _separator;
	private readonly string _sourceFilePath;
	private bool _disposed;

	private DebuggerExpressionIntelliSenseSession(
		DebuggerIntelliSenseWorkspace workspace,
		DocumentId documentId,
		ImmutableArray<DocumentId> linkedDocumentIds,
		SourceText sourceText,
		int insertionPosition,
		string separator,
		string sourceFilePath)
	{
		_workspace = workspace;
		_documentId = documentId;
		_linkedDocumentIds = linkedDocumentIds;
		_sourceText = sourceText;
		_insertionPosition = insertionPosition;
		_separator = separator;
		_sourceFilePath = sourceFilePath;
	}

	internal static async Task<DebuggerExpressionIntelliSenseSession> CreateAsync(Document sourceDocument, LinePosition sourceContextPosition, CancellationToken cancellationToken)
	{
		var sourceText = await sourceDocument.GetTextAsync(cancellationToken);
		var syntaxRoot = await sourceDocument.GetRequiredSyntaxRootAsync(cancellationToken);
		var contextPosition = GetClampedPosition(sourceText, sourceContextPosition);
		var (insertionPosition, separator) = GetInsertionPoint(syntaxRoot, sourceText, contextPosition);

		// If we ever wanted to show private members in completions. Note that reference assemblies complicate this, as they don't have private members, so we would have to resolve the implementation assemblies and replace them
		// var sourceCompilationOptions = sourceDocument.Project.CompilationOptions as CSharpCompilationOptions ?? throw new InvalidOperationException("The debugger expression document does not have C# compilation options.");
		// var compilationOptions = sourceCompilationOptions
		// 	.WithMetadataImportOptions(MetadataImportOptions.All)
		// 	.WithTopLevelBinderFlags(sourceCompilationOptions.TopLevelBinderFlags | BinderFlags.IgnoreAccessibility);
		// var solution = sourceDocument.Project.Solution.WithProjectCompilationOptions(sourceDocument.Project.Id, compilationOptions);
		var workspace = new DebuggerIntelliSenseWorkspace(sourceDocument.Project.Solution);

		return new DebuggerExpressionIntelliSenseSession(
			workspace,
			sourceDocument.Id,
			sourceDocument.GetLinkedDocumentIds(),
			sourceText,
			insertionPosition,
			separator,
			sourceDocument.FilePath ?? string.Empty);
	}

	public async Task<DebuggerExpressionAnalysisResult> AnalyzeAsync(string expressionText, CancellationToken cancellationToken = default)
	{
		var (document, expressionSpan) = CreateDocument(expressionText);
		var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken);
		var expressionSourceText = SourceText.From(expressionText);

		var classifiedSpans = await Classifier.GetClassifiedSpansAsync(document, expressionSpan, cancellationToken);
		var mappedClassifiedSpans = classifiedSpans
			.Where(span => IsWithinExpression(span.TextSpan, expressionSpan))
			.Select(span =>
			{
				var localSpan = new TextSpan(span.TextSpan.Start - expressionSpan.Start, span.TextSpan.Length);
				return new SharpIdeClassifiedSpan(expressionSourceText.GetLinePositionSpan(localSpan), span);
			})
			.ToImmutableArray();

		var diagnosticsBuilder = ImmutableArray.CreateBuilder<SharpIdeDiagnostic>();
		foreach (var diagnostic in semanticModel.GetDiagnostics(expressionSpan, cancellationToken))
		{
			if (diagnostic.Severity is DiagnosticSeverity.Hidden || diagnostic.Location.IsInSource is false || IsWithinExpression(diagnostic.Location.SourceSpan, expressionSpan) is false)
			{
				continue;
			}

			var sourceSpan = diagnostic.Location.SourceSpan;
			var localSpan = new TextSpan(sourceSpan.Start - expressionSpan.Start, sourceSpan.Length);
			diagnosticsBuilder.Add(new SharpIdeDiagnostic(expressionSourceText.GetLinePositionSpan(localSpan), diagnostic, _sourceFilePath));
		}

		var diagnostics = diagnosticsBuilder.ToImmutable();

		return new DebuggerExpressionAnalysisResult(mappedClassifiedSpans, diagnostics);
	}

	public async Task<bool> ShouldTriggerCompletionAsync(string expressionText, LinePosition caretPosition, CompletionTrigger trigger, CancellationToken cancellationToken = default)
	{
		var (document, expressionSpan) = CreateDocument(expressionText);
		var sourceText = await document.GetTextAsync(cancellationToken);
		var expressionSourceText = SourceText.From(expressionText);
		var position = expressionSpan.Start + GetClampedPosition(expressionSourceText, caretPosition);
		var completionService = CompletionService.GetService(document) ?? throw new InvalidOperationException("Completion service is not available for the debugger expression document.");
		var options = GetDebuggerCompletionOptions();

		return completionService.ShouldTriggerCompletion(
			document.Project,
			document.Project.Services,
			sourceText,
			position,
			trigger,
			options,
			document.Project.Solution.Options ?? OptionSet.Empty);
	}

	public async Task<DebuggerExpressionCompletionResult> GetCompletionsAsync(string expressionText, LinePosition caretPosition, CompletionTrigger trigger, CancellationToken cancellationToken = default)
	{
		var (document, expressionSpan) = CreateDocument(expressionText);
		var expressionSourceText = SourceText.From(expressionText);
		var position = expressionSpan.Start + GetClampedPosition(expressionSourceText, caretPosition);
		var completionService = CompletionService.GetService(document) ?? throw new InvalidOperationException("Completion service is not available for the debugger expression document.");
		var options = GetDebuggerCompletionOptions();
		var completionList = await completionService.GetCompletionsAsync(
			document,
			position,
			options,
			document.Project.Solution.Options,
			trigger,
			cancellationToken: cancellationToken);

		return new DebuggerExpressionCompletionResult(document, completionList, expressionText, expressionSpan.Start);
	}

	public ImmutableArray<SharpIdeCompletionItem> FilterCompletions(
		DebuggerExpressionCompletionResult completionResult,
		string expressionText,
		LinePosition caretPosition,
		CompletionTrigger trigger,
		CompletionFilterReason filterReason)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var sourceText = SourceText.From(expressionText);
		var position = GetClampedPosition(sourceText, caretPosition);
		var completionSpanStart = completionResult.CompletionList.Span.Start - completionResult.ExpressionStart;
		return RoslynAnalysis.FilterCompletions(sourceText, position, completionSpanStart, completionResult.CompletionList, trigger, filterReason);
	}

	public Task<CompletionDescription> GetCompletionDescriptionAsync(
		DebuggerExpressionCompletionResult completionResult,
		CompletionItem completionItem,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return RoslynAnalysis.GetCompletionDescription(completionResult.Document, completionItem, cancellationToken);
	}

	public async Task<DebuggerExpressionCompletionChange> GetCompletionChangeAsync(
		DebuggerExpressionCompletionResult completionResult,
		CompletionItem completionItem,
		string currentExpressionText,
		LinePosition currentCaretPosition,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var completionService = CompletionService.GetService(completionResult.Document) ?? throw new InvalidOperationException("Completion service is not available for the debugger expression document.");
		var completionChange = await completionService.GetChangeAsync(
			completionResult.Document,
			completionItem,
			commitCharacter: null,
			cancellationToken: cancellationToken);

		var expressionStart = completionResult.ExpressionStart;
		var expressionEnd = expressionStart + completionResult.ExpressionText.Length;
		var change = completionChange.TextChange;
		if (change.Span.Start < expressionStart || change.Span.End > expressionEnd)
		{
			throw new InvalidOperationException("The completion would modify source outside the debugger expression.");
		}

		var currentSourceText = SourceText.From(currentExpressionText);
		var currentCaret = GetClampedPosition(currentSourceText, currentCaretPosition);
		var relativeChangeStart = change.Span.Start - expressionStart;
		var relativeChangeEnd = change.Span.End - expressionStart;
		var replacementEnd = Math.Clamp(Math.Max(relativeChangeEnd, currentCaret), relativeChangeStart, currentSourceText.Length);
		var replacementText = change.NewText ?? string.Empty;
		var updatedText = currentSourceText.WithChanges(new TextChange(TextSpan.FromBounds(relativeChangeStart, replacementEnd), replacementText));

		var snapshotCaret = completionChange.NewPosition ?? change.Span.Start + replacementText.Length;
		var caretOffsetInReplacement = Math.Clamp(snapshotCaret - change.Span.Start, 0, replacementText.Length);
		var newCaretPosition = updatedText.Lines.GetLinePosition(relativeChangeStart + caretOffsetInReplacement);
		return new DebuggerExpressionCompletionChange(updatedText.ToString(), newCaretPosition);
	}

	public async Task<(ISymbol? Symbol, LinePositionSpan? Span)> LookupSymbolAsync(string expressionText, LinePosition expressionPosition, CancellationToken cancellationToken = default)
	{
		if (expressionText.Length is 0)
		{
			return (null, null);
		}

		var (document, expressionSpan) = CreateDocument(expressionText);
		var expressionSourceText = SourceText.From(expressionText);
		var localPosition = GetClampedPosition(expressionSourceText, expressionPosition);
		if (localPosition == expressionSourceText.Length)
		{
			localPosition--;
		}

		var absolutePosition = expressionSpan.Start + localPosition;
		var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, absolutePosition, cancellationToken);
		if (symbol is null)
		{
			return (null, null);
		}

		var root = await document.GetSyntaxRootAsync(cancellationToken)
			?? throw new InvalidOperationException("The debugger expression document does not support syntax.");
		var token = root.FindToken(absolutePosition, findInsideTrivia: true);
		if (IsWithinExpression(token.Span, expressionSpan) is false)
		{
			return (symbol, null);
		}

		var localSpan = new TextSpan(token.Span.Start - expressionSpan.Start, token.Span.Length);
		return (symbol, expressionSourceText.GetLinePositionSpan(localSpan));
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_workspace.Dispose();
	}

	private (Document Document, TextSpan ExpressionSpan) CreateDocument(string expressionText)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var expressionStart = _insertionPosition + _separator.Length;
		var insertedText = _separator + expressionText + StatementTerminator;
		var projectedText = _sourceText.WithChanges(new TextChange(new TextSpan(_insertionPosition, 0), insertedText));
		var solution = _workspace.CurrentSolution.WithDocumentText(_documentId, projectedText, PreservationMode.PreserveIdentity);

		foreach (var linkedDocumentId in _linkedDocumentIds)
		{
			if (solution.ContainsDocument(linkedDocumentId))
			{
				solution = solution.WithDocumentText(linkedDocumentId, projectedText, PreservationMode.PreserveIdentity);
			}
		}

		var document = solution.GetDocument(_documentId)
			?? throw new InvalidOperationException("The debugger expression document is no longer available.");
		return (document, new TextSpan(expressionStart, expressionText.Length));
	}

	private static CompletionOptions GetDebuggerCompletionOptions()
		=> CompletionOptions.Default with
		{
			FilterOutOfScopeLocals = false,
			ShowXmlDocCommentCompletion = false,
			CanAddImportStatement = false,
		};

	private static (int Position, string Separator) GetInsertionPoint(SyntaxNode syntaxRoot, SourceText sourceText, int contextPosition)
	{
		if (sourceText.Length is 0)
		{
			return (0, string.Empty);
		}

		var tokenPosition = Math.Clamp(contextPosition > 0 ? contextPosition - 1 : 0, 0, sourceText.Length - 1);
		var token = syntaxRoot.FindToken(tokenPosition, findInsideTrivia: true);
		var separator = StatementTerminator;
		var insertionPosition = token.FullSpan.End;

		if (contextPosition > token.Span.End &&
			token.IsKind(SyntaxKind.CloseBraceToken) &&
			token.Parent?.IsKind(SyntaxKind.Block) is true &&
			token.Parent.Parent is MemberDeclarationSyntax)
		{
			insertionPosition = contextPosition;
		}
		else if (token.IsKind(SyntaxKind.CloseBraceToken) && token.Parent?.IsKind(SyntaxKind.Block) is true)
		{
			insertionPosition = token.SpanStart;
		}
		else if (token.IsKind(SyntaxKind.SemicolonToken) && token.Parent is StatementSyntax)
		{
			separator = " ";
			insertionPosition = token.Parent.SpanStart;
		}

		return (Math.Clamp(insertionPosition, 0, sourceText.Length), separator);
	}

	private static int GetClampedPosition(SourceText sourceText, LinePosition linePosition)
	{
		if (sourceText.Lines.Count is 0)
		{
			return 0;
		}

		var lineIndex = Math.Clamp(linePosition.Line, 0, sourceText.Lines.Count - 1);
		var line = sourceText.Lines[lineIndex];
		var character = Math.Clamp(linePosition.Character, 0, line.Span.Length);
		return line.Start + character;
	}

	private static bool IsWithinExpression(TextSpan span, TextSpan expressionSpan) => span.Start >= expressionSpan.Start && span.End <= expressionSpan.End;
}
