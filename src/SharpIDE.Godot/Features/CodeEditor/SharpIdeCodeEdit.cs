using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Rename.ConflictEngine;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using SharpIDE.Application;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Problems;
using SharpIDE.Godot.Features.SymbolLookup;
using SharpIDE.RazorAccess;
using Task = System.Threading.Tasks.Task;

namespace SharpIDE.Godot.Features.CodeEditor;

#pragma warning disable VSTHRD101
public partial class SharpIdeCodeEdit : CodeEdit
{
	[Signal]
	public delegate void CodeFixesRequestedEventHandler();

	private int _currentLine;
	private int _selectionStartCol;
	private int _selectionEndCol;
	
	public SharpIdeSolutionModel? Solution { get; set; }
	public SharpIdeFile SharpIdeFile => _currentFile;
	private SharpIdeFile _currentFile = null!;
	
	private CustomHighlighter _syntaxHighlighter = new();
	private PopupMenu _popupMenu = null!;

	private ImmutableArray<SharpIdeDiagnostic> _fileDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _projectDiagnosticsForFile = [];
	private ImmutableArray<CodeAction> _currentCodeActionsInPopup = [];
	private bool _fileChangingSuppressBreakpointToggleEvent;
	private bool _settingWholeDocumentTextSuppressLineEditsEvent; // A dodgy workaround - setting the whole document doesn't guarantee that the line count stayed the same etc. We are still going to have broken highlighting. TODO: Investigate getting minimal text change ranges, and change those ranges only
	private bool _fileDeleted;
	
    [Inject] private readonly IdeOpenTabsFileManager _openTabsFileManager = null!;
    [Inject] private readonly RunService _runService = null!;
    [Inject] private readonly RoslynAnalysis _roslynAnalysis = null!;
    [Inject] private readonly IdeCodeActionService _ideCodeActionService = null!;
    [Inject] private readonly FileChangedService _fileChangedService = null!;
    [Inject] private readonly IdeCompletionService _ideCompletionService = null!;
	
	public override void _Ready()
	{
		CodeCompletionPrefixes = ["."];
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
		LinesEditedFrom += OnLinesEditedFrom;
		GlobalEvents.Instance.SolutionAltered.Subscribe(OnSolutionAltered);
	}

	private CancellationTokenSource _solutionAlteredCts = new();
	private async Task OnSolutionAltered()
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnSolutionAltered)}");
		if (_currentFile is null) return;
		if (_fileDeleted) return;
		GD.Print($"[{_currentFile.Name}] Solution altered, updating project diagnostics for file");
		await _solutionAlteredCts.CancelAsync();
		_solutionAlteredCts = new CancellationTokenSource();
		var ct = _solutionAlteredCts.Token;
		var documentSyntaxHighlighting = _roslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile, ct);
		var razorSyntaxHighlighting = _roslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile, ct);
		await Task.WhenAll(documentSyntaxHighlighting, razorSyntaxHighlighting);
		await this.InvokeAsync(async () => SetSyntaxHighlightingModel(await documentSyntaxHighlighting, await razorSyntaxHighlighting));
		var documentDiagnostics = await _roslynAnalysis.GetDocumentDiagnostics(_currentFile, ct);
		await this.InvokeAsync(() => SetDiagnostics(documentDiagnostics));
		var projectDiagnostics = await _roslynAnalysis.GetProjectDiagnosticsForFile(_currentFile, ct);
		await this.InvokeAsync(() => SetProjectDiagnostics(projectDiagnostics));
	}

	public enum LineEditOrigin
	{
		StartOfLine,
		EndOfLine,
		Unknown
	}
	// Line removed - fromLine 55, toLine 54
	// Line added - fromLine 54, toLine 55
	// Multi cursor gets a single line event for each
	// problem is 10 to 11 gets returned for 'Enter' at the start of line 10, as well as 'Enter' at the end of line 10
	// This means that the line that moves down needs to be based on whether the new line was from the start or end of the line
	private void OnLinesEditedFrom(long fromLine, long toLine)
	{
		if (fromLine == toLine) return;
		if (_settingWholeDocumentTextSuppressLineEditsEvent) return;
		var fromLineText = GetLine((int)fromLine);
		var caretPosition = this.GetCaretPosition();
		var textFrom0ToCaret = fromLineText[..caretPosition.col];
		var caretPositionEnum = LineEditOrigin.Unknown;
		if (string.IsNullOrWhiteSpace(textFrom0ToCaret))
		{
			caretPositionEnum = LineEditOrigin.StartOfLine;
		}
		else
		{
			var textfromCaretToEnd = fromLineText[caretPosition.col..];
			if (string.IsNullOrWhiteSpace(textfromCaretToEnd))
			{
				caretPositionEnum = LineEditOrigin.EndOfLine;
			}
		}
		//GD.Print($"Lines edited from {fromLine} to {toLine}, origin: {caretPositionEnum}, current caret position: {caretPosition}");
		_syntaxHighlighter.LinesChanged(fromLine, toLine, caretPositionEnum);
	}

	public override void _ExitTree()
	{
		_currentFile?.FileContentsChangedExternally.Unsubscribe(OnFileChangedExternally);
		_currentFile?.FileDeleted.Unsubscribe(OnFileDeleted);
		GlobalEvents.Instance.SolutionAltered.Unsubscribe(OnSolutionAltered);
		if (_currentFile is not null) _openTabsFileManager.CloseFile(_currentFile);
	}

	private void OnBreakpointToggled(long line)
	{
		if (_fileChangingSuppressBreakpointToggleEvent) return;
		var lineInt = (int)line;
		var breakpointAdded = IsLineBreakpointed(lineInt);
		var lineForDebugger = lineInt + 1; // Godot is 0-indexed, Debugging is 1-indexed
		var breakpoints = _runService.Breakpoints.GetOrAdd(_currentFile, []); 
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

	private void OnSymbolValidate(string symbol)
	{
		GD.Print($"Symbol validating: {symbol}");
		//var valid = symbol.Contains(' ') is false;
		//SetSymbolLookupWordAsValid(valid);
		SetSymbolLookupWordAsValid(true);
	}
	
	private void OnCaretChanged()
	{
		_selectionStartCol = GetSelectionFromColumn();
		_selectionEndCol = GetSelectionToColumn();
		_currentLine = GetCaretLine();
		// GD.Print($"Selection changed to line {_currentLine}, start {_selectionStartCol}, end {_selectionEndCol}");
	}

	private void OnTextChanged()
	{
		_ = Task.GodotRun(async () =>
		{
			var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnTextChanged)}");
			_currentFile.IsDirty.Value = true;
			await _fileChangedService.SharpIdeFileChanged(_currentFile, Text, FileChangeType.IdeUnsavedChange);
			__?.Dispose();
		});
	}

	// TODO: This is now significantly slower, invoke -> text updated in editor
	private void OnCodeFixSelected(long id)
	{
		GD.Print($"Code fix selected: {id}");
		var codeAction = _currentCodeActionsInPopup[(int)id];
		if (codeAction is null) return;
		
		_ = Task.GodotRun(async () =>
		{
			await _ideCodeActionService.ApplyCodeAction(codeAction);
		});
	}

	private async Task OnFileChangedExternally(SharpIdeFileLinePosition? linePosition)
	{
		if (_fileDeleted) return; // We have QueueFree'd this node, however it may not have been freed yet.
		var fileContents = await _openTabsFileManager.GetFileTextAsync(_currentFile);
		await this.InvokeAsync(() =>
		{
			(int line, int col) currentCaretPosition = linePosition is null ? GetCaretPosition() : (linePosition.Value.Line, linePosition.Value.Column);
			var vScroll = GetVScroll();
			BeginComplexOperation();
			_settingWholeDocumentTextSuppressLineEditsEvent = true;
			SetText(fileContents);
			_settingWholeDocumentTextSuppressLineEditsEvent = false;
			SetCaretLine(currentCaretPosition.line);
			SetCaretColumn(currentCaretPosition.col);
			SetVScroll(vScroll);
			EndComplexOperation();
		});
	}

	public void SetFileLinePosition(SharpIdeFileLinePosition fileLinePosition)
	{
		var line = fileLinePosition.Line;
		var column = fileLinePosition.Column;
		SetCaretLine(line);
		SetCaretColumn(column);
		Callable.From(() =>
		{
			GrabFocus();
			CenterViewportToCaret();
		}).CallDeferred();
	}

	// TODO: Ensure not running on UI thread
	public async Task SetSharpIdeFile(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition = null)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // get off the UI thread
		using var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(SetSharpIdeFile)}");
		_currentFile = file;
		var readFileTask = _openTabsFileManager.GetFileTextAsync(file);
		_currentFile.FileContentsChangedExternally.Subscribe(OnFileChangedExternally);
		_currentFile.FileDeleted.Subscribe(OnFileDeleted);
		
		var syntaxHighlighting = _roslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
		var razorSyntaxHighlighting = _roslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile);
		var diagnostics = _roslynAnalysis.GetDocumentDiagnostics(_currentFile);
		var projectDiagnosticsForFile = _roslynAnalysis.GetProjectDiagnosticsForFile(_currentFile);
		await readFileTask;
		var setTextTask = this.InvokeAsync(async () =>
		{
			_fileChangingSuppressBreakpointToggleEvent = true;
			SetText(await readFileTask);
			_fileChangingSuppressBreakpointToggleEvent = false;
			if (fileLinePosition is not null) SetFileLinePosition(fileLinePosition.Value);
		});
		_ = Task.GodotRun(async () =>
		{
			await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, setTextTask); // Text must be set before setting syntax highlighting
			await this.InvokeAsync(async () => SetSyntaxHighlightingModel(await syntaxHighlighting, await razorSyntaxHighlighting));
			await diagnostics;
			await this.InvokeAsync(async () => SetDiagnostics(await diagnostics));
			await projectDiagnosticsForFile;
			await this.InvokeAsync(async () => SetProjectDiagnostics(await projectDiagnosticsForFile));
		});
	}

	private async Task OnFileDeleted()
	{
		_fileDeleted = true;
		QueueFree();
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
		foreach (var sharpIdeDiagnostic in _fileDiagnostics.ConcatFast(_projectDiagnosticsForFile))
		{
			var line = sharpIdeDiagnostic.Span.Start.Line;
			var startCol = sharpIdeDiagnostic.Span.Start.Character;
			var endCol = sharpIdeDiagnostic.Span.End.Character;
			var color = sharpIdeDiagnostic.Diagnostic.Severity switch
			{
				DiagnosticSeverity.Error => new Color(1, 0, 0),
				DiagnosticSeverity.Warning => new Color("ffb700"),
				_ => new Color(0, 1, 0) // Info or other
			};
			UnderlineRange(line, startCol, endCol, color);
		}
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		CloseSymbolHoverWindow();
		// Let each open tab respond to this event
		if (@event.IsActionPressed(InputStringNames.SaveAllFiles))
		{
			_ = Task.GodotRun(async () =>
			{
				await _fileChangedService.SharpIdeFileChanged(_currentFile, Text, FileChangeType.IdeSaveToDisk);
			});
		}
		// Now we filter to only the focused tab
		if (HasFocus() is false) return;
		if (@event.IsActionPressed(InputStringNames.CodeFixes))
		{
			EmitSignalCodeFixesRequested();
		}
		else if (@event.IsActionPressed(InputStringNames.SaveFile) && @event.IsActionPressed(InputStringNames.SaveAllFiles) is false)
		{
			_ = Task.GodotRun(async () =>
			{
				await _fileChangedService.SharpIdeFileChanged(_currentFile, Text, FileChangeType.IdeSaveToDisk);
			});
		}
	}

	

	private readonly Color _breakpointLineColor = new Color("3a2323");
	private readonly Color _executingLineColor = new Color("665001");
	public void SetLineColour(int line)
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

	[RequiresGodotUiThread]
	private void SetDiagnostics(ImmutableArray<SharpIdeDiagnostic> diagnostics)
	{
		_fileDiagnostics = diagnostics;
		QueueRedraw();
	}
	
	[RequiresGodotUiThread]
	private void SetProjectDiagnostics(ImmutableArray<SharpIdeDiagnostic> diagnostics)
	{
		_projectDiagnosticsForFile = diagnostics;
		QueueRedraw();
	}

	[RequiresGodotUiThread]
	private void SetSyntaxHighlightingModel(ImmutableArray<SharpIdeClassifiedSpan> classifiedSpans, ImmutableArray<SharpIdeRazorClassifiedSpan> razorClassifiedSpans)
	{
		_syntaxHighlighter.SetHighlightingData(classifiedSpans, razorClassifiedSpans);
		//_syntaxHighlighter.ClearHighlightingCache();
		_syntaxHighlighter.UpdateCache(); // I don't think this does anything, it will call _UpdateCache which we have not implemented
		SyntaxHighlighter = null;
		SyntaxHighlighter = _syntaxHighlighter; // Reassign to trigger redraw
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
			var codeActions = await _roslynAnalysis.GetCodeFixesForDocumentAtPosition(_currentFile, linePos);
			await this.InvokeAsync(() =>
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
			});
		});
	}

	public override void _ConfirmCodeCompletion(bool replace)
	{
		var selectedIndex = GetCodeCompletionSelectedIndex();
		var selectedText = GetCodeCompletionOption(selectedIndex);
		if (selectedText is null) return;
		var completionItem = selectedText["default_value"].As<RefCountedContainer<CompletionItem>>().Item;
		_ = Task.GodotRun(async () =>
		{
			await _ideCompletionService.ApplyCompletion(_currentFile, completionItem);
		});
		CancelCodeCompletion();
	}

	private void OnCodeCompletionRequested()
	{
		var (caretLine, caretColumn) = GetCaretPosition();
		
		GD.Print($"Code completion requested at line {caretLine}, column {caretColumn}");
		_ = Task.GodotRun(async () =>
		{
			var linePos = new LinePosition(caretLine, caretColumn);
				
			var completions = await _roslynAnalysis.GetCodeCompletionsForDocumentAtPosition(_currentFile, linePos);
			await this.InvokeAsync(() =>
			{
				foreach (var completionItem in completions.ItemsList)
				{
					var symbolKindString = CollectionExtensions.GetValueOrDefault(completionItem.Properties, "SymbolKind");
					var symbolKind = symbolKindString is null ? null : (SymbolKind?)int.Parse(symbolKindString);
					var godotCompletionType = symbolKind switch
					{
						SymbolKind.Method => CodeCompletionKind.Function,
						SymbolKind.NamedType => CodeCompletionKind.Class,
						SymbolKind.Local => CodeCompletionKind.Variable,
						SymbolKind.Property => CodeCompletionKind.Member,
						SymbolKind.Field => CodeCompletionKind.Member,
						_ => CodeCompletionKind.PlainText
					};
					var icon = GetIconForSymbolKind(symbolKind);
					AddCodeCompletionOption(godotCompletionType, completionItem.DisplayText, completionItem.DisplayText, icon: icon, value: new RefCountedContainer<CompletionItem>(completionItem));
				}
				// partially working - displays menu only when caret is what CodeEdit determines as valid
				UpdateCodeCompletionOptions(true);
				//RequestCodeCompletion(true);
				GD.Print($"Found {completions.ItemsList.Count} completions, displaying menu");
			});
		});
	}
	
	private (int line, int col) GetCaretPosition()
	{
		var caretColumn = GetCaretColumn();
		var caretLine = GetCaretLine();
		return (caretLine, caretColumn);
	}
}