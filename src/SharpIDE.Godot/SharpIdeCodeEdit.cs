using System.Collections.Immutable;
using Ardalis.GuardClauses;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using Task = System.Threading.Tasks.Task;

namespace SharpIDE.Godot;

public partial class SharpIdeCodeEdit : CodeEdit
{
	[Signal]
	public delegate void CodeFixesRequestedEventHandler();

	private int _currentLine;
	private int _selectionStartCol;
	private int _selectionEndCol;
	
	public SharpIdeSolutionModel? Solution { get; set; }
	private SharpIdeFile _currentFile = null!;
	
	private CustomHighlighter _syntaxHighlighter = new();
	private PopupMenu _popupMenu = null!;

	private ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)> _diagnostics = [];
	private ImmutableArray<CodeAction> _currentCodeActionsInPopup = [];
	private ExecutionStopInfo? _executionStopInfo;
	private bool _fileChangingSuppressBreakpointToggleEvent;
	
	public override void _Ready()
	{
		SyntaxHighlighter = _syntaxHighlighter;
		_popupMenu = GetNode<PopupMenu>("CodeFixesMenu");
		_popupMenu.IdPressed += OnCodeFixSelected;
		CodeCompletionRequested += OnCodeCompletionRequested;
		CodeFixesRequested += OnCodeFixesRequested;
		BreakpointToggled += OnBreakpointToggled;
		CaretChanged += OnCaretChanged;
		TextChanged += OnTextChanged;
		SymbolHovered += OnSymbolHovered;
		SymbolValidate += OnSymbolValidate;
		SymbolLookup += OnSymbolLookup;
		GlobalEvents.DebuggerExecutionStopped += OnDebuggerExecutionStopped;
	}

	private async Task OnDebuggerExecutionStopped(ExecutionStopInfo executionStopInfo)
	{
		Guard.Against.Null(Solution, nameof(Solution));
		if (executionStopInfo.FilePath != _currentFile.Path)
		{
			var file = Solution.AllFiles.Single(s => s.Path == executionStopInfo.FilePath);
			await this.InvokeAsync(async () => await SetSharpIdeFile(file));
		}
		var lineInt = executionStopInfo.Line - 1; // Debugging is 1-indexed, Godot is 0-indexed
		Guard.Against.Negative(lineInt, nameof(lineInt));
		_executionStopInfo = executionStopInfo;
		
		await this.InvokeAsync(() =>
		{
			SetLineBackgroundColor(lineInt, new Color("665001"));
			SetLineAsExecuting(lineInt, true);
		});
	}

	private void OnBreakpointToggled(long line)
	{
		if (_fileChangingSuppressBreakpointToggleEvent) return;
		var lineInt = (int)line;
		var breakpointAdded = IsLineBreakpointed(lineInt);
		var lineForDebugger = lineInt + 1; // Godot is 0-indexed, Debugging is 1-indexed
		var breakpoints = Singletons.RunService.Breakpoints.GetOrAdd(_currentFile, []); 
		if (breakpointAdded)
		{
			breakpoints.Add(new Breakpoint { Line = lineForDebugger } );
		}
		else
		{
			var breakpoint = breakpoints.Single(b => b.Line == lineForDebugger);
			breakpoints.Remove(breakpoint);
		}
		SetLineColour(lineInt);
		GD.Print($"Breakpoint {(breakpointAdded ? "added" : "removed")} at line {lineForDebugger}");
	}

	private void OnSymbolLookup(string symbol, long line, long column)
	{
		GD.Print($"Symbol lookup requested: {symbol} at line {line}, column {column}");
	}

	private void OnSymbolValidate(string symbol)
	{
		GD.Print($"Symbol validating: {symbol}");
		SetSymbolLookupWordAsValid(true);
	}

	private void OnSymbolHovered(string symbol, long line, long column)
	{
		GD.Print($"Symbol hovered: {symbol}");
	}

	private void OnCaretChanged()
	{
		_selectionStartCol = GetSelectionFromColumn();
		_selectionEndCol = GetSelectionToColumn();
		_currentLine = GetCaretLine();
		GD.Print($"Selection changed to line {_currentLine}, start {_selectionStartCol}, end {_selectionEndCol}");
	}

	private void OnTextChanged()
	{
		// update the MSBuildWorkspace
		RoslynAnalysis.UpdateDocument(_currentFile, Text);
		_ = Task.GodotRun(async () =>
		{
			var syntaxHighlighting = await RoslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
			var diagnostics = await RoslynAnalysis.GetDocumentDiagnostics(_currentFile);
			await RoslynAnalysis.UpdateSolutionDiagnostics();
			Callable.From(() =>
			{
				SetSyntaxHighlightingModel(syntaxHighlighting);
				SetDiagnosticsModel(diagnostics);
			}).CallDeferred();
		});
	}

	private void OnCodeFixSelected(long id)
	{
		GD.Print($"Code fix selected: {id}");
		var codeAction = _currentCodeActionsInPopup[(int)id];
		if (codeAction is null) return;
		var currentCaretPosition = GetCaretPosition();
		var vScroll = GetVScroll();
		_ = Task.GodotRun(async () =>
		{
			await RoslynAnalysis.ApplyCodeActionAsync(codeAction);
			var fileContents = await File.ReadAllTextAsync(_currentFile.Path);
			var syntaxHighlighting = await RoslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
			var diagnostics = await RoslynAnalysis.GetDocumentDiagnostics(_currentFile);
			Callable.From(() =>
			{
				BeginComplexOperation();
				SetText(fileContents);
				SetSyntaxHighlightingModel(syntaxHighlighting);
				SetDiagnosticsModel(diagnostics);
				SetCaretLine(currentCaretPosition.line);
				SetCaretColumn(currentCaretPosition.col);
				SetVScroll(vScroll);
				EndComplexOperation();
			}).CallDeferred();
		});
	}

	public async Task SetSharpIdeFile(SharpIdeFile file)
	{
		_currentFile = file;
		var fileContents = await File.ReadAllTextAsync(_currentFile.Path);
		_fileChangingSuppressBreakpointToggleEvent = true;
		SetText(fileContents);
		_fileChangingSuppressBreakpointToggleEvent = false;
		var syntaxHighlighting = await RoslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
		SetSyntaxHighlightingModel(syntaxHighlighting);
		var diagnostics = await RoslynAnalysis.GetDocumentDiagnostics(_currentFile);
		SetDiagnosticsModel(diagnostics);
	}
	
	public void UnderlineRange(int line, int caretStartCol, int caretEndCol, Color color, float thickness = 1.5f)
	{
		if (line < 0 || line >= GetLineCount())
			return;

		if (caretStartCol > caretEndCol) // something went wrong
			return;

		// Clamp columns to line length
		int lineLength = GetLine(line).Length;
		caretStartCol = Mathf.Clamp(caretStartCol, 0, lineLength);
		caretEndCol   = Mathf.Clamp(caretEndCol, 0, lineLength);
		
		// GetRectAtLineColumn returns the rectangle for the character before the column passed in, or the first character if the column is 0.
		var startRect = GetRectAtLineColumn(line, caretStartCol);
		var endRect = GetRectAtLineColumn(line, caretEndCol);
		//DrawLine(startRect.Position, startRect.End, color);
		//DrawLine(endRect.Position, endRect.End, color);
		
		var startPos = startRect.End;
		if (caretStartCol is 0)
		{
			startPos.X -= startRect.Size.X;
		}
		var endPos = endRect.End;
		startPos.Y -= 3;
		endPos.Y   -= 3;
		if (caretStartCol == caretEndCol)
		{
			endPos.X += 10;
		}
		DrawDashedLine(startPos, endPos, color, thickness);
		//DrawLine(startPos, endPos, color, thickness);
	}
	public override void _Draw()
	{
		//UnderlineRange(_currentLine, _selectionStartCol, _selectionEndCol, new Color(1, 0, 0));
		foreach (var (fileSpan, diagnostic) in _diagnostics)
		{
			if (diagnostic.Location.IsInSource)
			{
				var line = fileSpan.StartLinePosition.Line;
				var startCol = fileSpan.StartLinePosition.Character;
				var endCol = fileSpan.EndLinePosition.Character;
				var color = diagnostic.Severity switch
				{
					DiagnosticSeverity.Error => new Color(1, 0, 0),
					DiagnosticSeverity.Warning => new Color("ffb700"),
					_ => new Color(0, 1, 0) // Info or other
				};
				UnderlineRange(line, startCol, endCol, color);
			}
		}
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event.IsActionPressed(InputStringNames.CodeFixes))
		{
			EmitSignalCodeFixesRequested();
		}
		else if (@event.IsActionPressed(InputStringNames.StepOver))
		{
			SendDebuggerStepOver();
		}
	}

	private void SendDebuggerStepOver()
	{
		if (_executionStopInfo is null) return;
		var godotLine = _executionStopInfo.Line - 1;
		SetLineAsExecuting(godotLine, false);
		SetLineColour(godotLine);
		var threadId = _executionStopInfo.ThreadId;
		_executionStopInfo = null;
		_ = Task.GodotRun(async () =>
		{
			await Singletons.RunService.SendDebuggerStepOver(threadId);
		});
	}

	private readonly Color _breakpointLineColor = new Color("3a2323");
	private readonly Color _executingLineColor = new Color("665001");
	private void SetLineColour(int line)
	{
		var breakpointed = IsLineBreakpointed(line);
		var executing = IsLineExecuting(line);
		var lineColour = (breakpointed, executing) switch
		{
			(_, true) => _executingLineColor,
			(true, false) => _breakpointLineColor,
			(false, false) => Colors.Transparent
		};
		SetLineBackgroundColor(line, lineColour);
	}

	private void SetDiagnosticsModel(ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)> diagnostics)
	{
		_diagnostics = diagnostics;
	}

	private void SetSyntaxHighlightingModel(IEnumerable<(FileLinePositionSpan fileSpan, ClassifiedSpan classifiedSpan)> classifiedSpans)
	{
		_syntaxHighlighter.ClassifiedSpans = classifiedSpans;
		Callable.From(() =>
		{
			_syntaxHighlighter.ClearHighlightingCache();
			//_syntaxHighlighter.UpdateCache();
			SyntaxHighlighter = null;
			SyntaxHighlighter = _syntaxHighlighter; // Reassign to trigger redraw
			GD.Print("Provided syntax highlighting");
		}).CallDeferred();
	}

	private void OnCodeFixesRequested()
	{
		var (caretLine, caretColumn) = GetCaretPosition();
		var popupMenuPosition = GetCaretDrawPos() with { X = 0 } + GetGlobalPosition();
		_popupMenu.Position = new Vector2I((int)popupMenuPosition.X, (int)popupMenuPosition.Y);
		_popupMenu.Clear();
		_popupMenu.AddItem("Getting Context Actions...", 0);
		_popupMenu.Popup();
		GD.Print($"Code fixes requested at line {caretLine}, column {caretColumn}");
		_ = Task.GodotRun(async () =>
		{
			var linePos = new LinePosition(caretLine, caretColumn);
			var codeActions = await RoslynAnalysis.GetCodeFixesForDocumentAtPosition(_currentFile, linePos);
			Callable.From(() =>
			{
				_popupMenu.Clear();
				foreach (var (index, codeAction) in codeActions.Index())
				{
					_currentCodeActionsInPopup = codeActions;
					_popupMenu.AddItem(codeAction.Title, index);
					//_popupMenu.SetItemMetadata(menuItem, codeAction);
				}

				if (codeActions.Length is not 0) _popupMenu.SetFocusedItem(0);
				GD.Print($"Code fixes found: {codeActions.Length}, displaying menu");
			}).CallDeferred();
		});
	}

	private void OnCodeCompletionRequested()
	{
		var (caretLine, caretColumn) = GetCaretPosition();
		
		GD.Print($"Code completion requested at line {caretLine}, column {caretColumn}");
		_ = Task.GodotRun(async () =>
		{
			var linePos = new LinePosition(caretLine, caretColumn);
				
			var completions = await RoslynAnalysis.GetCodeCompletionsForDocumentAtPosition(_currentFile, linePos);
			Callable.From(() =>
			{
				foreach (var completionItem in completions.ItemsList)
				{
					AddCodeCompletionOption(CodeCompletionKind.Class, completionItem.DisplayText, completionItem.DisplayText);
				}
				// partially working - displays menu only when caret is what CodeEdit determines as valid
				UpdateCodeCompletionOptions(true);
				//RequestCodeCompletion(true);
				GD.Print($"Found {completions.ItemsList.Count} completions, displaying menu");
			}).CallDeferred();
		});
	}
	
	private (int line, int col) GetCaretPosition()
	{
		var caretColumn = GetCaretColumn();
		var caretLine = GetCaretLine();
		return (caretLine, caretColumn);
	}
}