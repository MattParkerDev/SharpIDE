using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.Events;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private EventWrapper<CompletionTrigger, SourceText, (int, int), Task> CustomCodeCompletionRequested { get; } = new((_, _, _) => Task.CompletedTask);
	private CodeCompletionPopup _completionPopup = null!;
	private CompletionList? _completionList;
	private Document? _completionResultDocument;
	private CompletionTrigger? _completionTrigger;

	private void InitializeCompletionPopup()
	{
		_completionPopup = new CodeCompletionPopup(
			this,
			_aboveCanvasItemRid!.Value,
			_completionDescriptionWindow,
			_completionDescriptionLabel,
			_syntaxHighlighter,
			GetCompletionDescriptionAsync,
			trigger =>
			{
				SyncTextFromEditor();
				if (_currentSourceText is null) return;
				_completionTrigger = trigger;
				CustomCodeCompletionRequested.InvokeParallelFireAndForget(trigger, _currentSourceText, GetCaretPosition());
			},
			ApplySelectedCodeCompletion);
	}

	private void ResetCompletionPopupState()
	{
		_completionPopup.Reset();
		_completionList = null;
		_completionResultDocument = null;
		_completionTrigger = null;
	}

	private bool CompletionsPopupTryConsumeGuiInput(InputEvent @event) => _completionPopup.TryConsumeGuiInput(@event);

	private void DrawCompletionsPopup() => _completionPopup.Draw();

	private async Task CustomFilterCodeCompletionCandidates(CompletionFilterReason filterReason)
	{
		if (_completionList is null || _completionList.ItemsList.Count is 0 || _completionTrigger is null)
		{
			return;
		}

		var (cursorPosition, sourceText) = await this.InvokeAsync(() => (GetCaretPosition(), _currentSourceText));
		if (sourceText is null) return;
		var filteredCompletions = RoslynAnalysis.FilterCompletions(
			sourceText,
			new LinePosition(cursorPosition.line, cursorPosition.col),
			_completionList,
			_completionTrigger.Value,
			filterReason);
		await this.InvokeAsync(() => _completionPopup.SetOptions(filteredCompletions));
	}

	private async Task OnCodeCompletionRequested(CompletionTrigger completionTrigger, SourceText documentText, (int, int) caretPosition)
	{
		var linePosition = new LinePosition(caretPosition.Item1, caretPosition.Item2);
		var result = await _roslynAnalysis.GetCodeCompletionsForDocumentAtPosition(_currentFile, documentText, linePosition, completionTrigger);
		var triggerPosition = await this.InvokeAsync(() => GetPosAtLineColumn(result.LinePosition.Line, result.LinePosition.Character));
		_completionList = result.CompletionList;
		_completionResultDocument = result.Document;
		_completionTrigger = completionTrigger;
		await this.InvokeAsync(() => _completionPopup.SetTriggerPosition(triggerPosition));
		var filterReason = completionTrigger.Kind switch
		{
			CompletionTriggerKind.Insertion => CompletionFilterReason.Insertion,
			CompletionTriggerKind.Deletion => CompletionFilterReason.Deletion,
			CompletionTriggerKind.InvokeAndCommitIfUnique => CompletionFilterReason.Other,
			_ => throw new ArgumentOutOfRangeException(nameof(completionTrigger.Kind), completionTrigger.Kind, null),
		};
		await CustomFilterCodeCompletionCandidates(filterReason);
	}

	private Task<CompletionDescription> GetCompletionDescriptionAsync(CompletionItem completionItem, CancellationToken cancellationToken)
	{
		var document = _completionResultDocument ?? throw new InvalidOperationException("The completion result document is unavailable.");
		return RoslynAnalysis.GetCompletionDescription(document, completionItem, cancellationToken);
	}

	private void ApplySelectedCodeCompletion(CompletionItem completionItem)
	{
		var document = _completionResultDocument ?? throw new InvalidOperationException("The completion result document is unavailable.");
		_completionList = null;
		_completionResultDocument = null;
		_completionTrigger = null;
		_ = Task.GodotRun(async () =>
		{
			var completionText = await document.GetTextAsync();
			var (change, newCaretPosition) = await RoslynAnalysis.GetCompletionChange(completionItem, document);
			await this.InvokeAsync(() => ApplyCompletionChange(completionText, change, newCaretPosition));
		});
	}

	/// change and newCaretPosition are relative to completionText, the text the completion list was computed for
	[RequiresGodotUiThread]
	private void ApplyCompletionChange(SourceText completionText, TextChange change, int newCaretPosition)
	{
		SyncTextFromEditor();
		if (_currentSourceText is null) return;
		// The user has usually typed since the list was computed (e.g. filter text) - map the change onto the current text, replacing what was typed
		if (TextDiff.TryMapChangeForward(completionText, _currentSourceText, change, out var mappedChange) is false)
		{
			GD.PrintErr($"[{_currentFile.Name.Value}] Completion not applied - the text around it changed since the completion list was computed");
			return;
		}
		var caretOffset = mappedChange.Span.Start + (newCaretPosition - change.Span.Start);
		ApplyChanges(_currentSourceText, [mappedChange], caretOffset);
	}
}
