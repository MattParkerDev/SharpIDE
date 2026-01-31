using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Threading;
using ObservableCollections;
using R3;
using Roslyn.Utilities;
using SharpIDE.Application;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.NavigationHistory;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using Task = System.Threading.Tasks.Task;

namespace SharpIDE.Godot.Features.CodeEditor;

#pragma warning disable VSTHRD101
public partial class SharpIdeCodeEdit : CodeEdit
{
	[Signal]
	public delegate void CodeFixesRequestedEventHandler();
	
	public SharpIdeSolutionModel? Solution { get; set; }
	public SharpIdeFile SharpIdeFile => _currentFile;
	private SharpIdeFile _currentFile = null!;
	
	private CustomHighlighter _syntaxHighlighter = new();
	private PopupMenu _popupMenu = null!;
	private CanvasItem _aboveCanvasItem = null!;
	private Rid? _aboveCanvasItemRid = null!;

	private ImmutableArray<SharpIdeDiagnostic> _fileDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _fileAnalyzerDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _projectDiagnosticsForFile = [];
	private ImmutableArray<CodeAction> _currentCodeActionsInPopup = [];
	private bool _fileChangingSuppressBreakpointToggleEvent;
	private bool _settingWholeDocumentTextSuppressLineEditsEvent; // A dodgy workaround - setting the whole document doesn't guarantee that the line count stayed the same etc. We are still going to have broken highlighting. TODO: Investigate getting minimal text change ranges, and change those ranges only
	private bool _fileDeleted;
	private IDisposable? _projectDiagnosticsObserveDisposable;
	
    [Inject] private readonly IdeOpenTabsFileManager _openTabsFileManager = null!;
    [Inject] private readonly RunService _runService = null!;
    [Inject] private readonly RoslynAnalysis _roslynAnalysis = null!;
    [Inject] private readonly IdeCodeActionService _ideCodeActionService = null!;
    [Inject] private readonly FileChangedService _fileChangedService = null!;
    [Inject] private readonly IdeApplyCompletionService _ideApplyCompletionService = null!;
    [Inject] private readonly IdeNavigationHistoryService _navigationHistoryService = null!;
    [Inject] private readonly EditorCaretPositionService _editorCaretPositionService = null!;

	public SharpIdeCodeEdit()
	{
		_selectionChangedQueue = new AsyncBatchingWorkQueue(TimeSpan.FromMilliseconds(150), ProcessSelectionChanged, IAsynchronousOperationListener.Instance, CancellationToken.None);
	}

	public override void _Ready()
	{
		UpdateEditorThemeForCurrentTheme();
		SyntaxHighlighter = _syntaxHighlighter;
		_popupMenu = GetNode<PopupMenu>("CodeFixesMenu");
		_aboveCanvasItem = GetNode<CanvasItem>("%AboveCanvasItem");
		_aboveCanvasItemRid = _aboveCanvasItem.GetCanvasItem();
		RenderingServer.Singleton.CanvasItemSetParent(_aboveCanvasItemRid.Value, GetCanvasItem());
		_popupMenu.IdPressed += OnCodeFixSelected;
		CustomCodeCompletionRequested.Subscribe(OnCodeCompletionRequested);
		CodeFixesRequested += OnCodeFixesRequested;
		BreakpointToggled += OnBreakpointToggled;
		CaretChanged += OnCaretChanged;
		TextChanged += OnTextChanged;
		FocusEntered += OnFocusEntered;
		SymbolHovered += OnSymbolHovered;
		SymbolValidate += OnSymbolValidate;
		SymbolLookup += OnSymbolLookup;
		LinesEditedFrom += OnLinesEditedFrom;
		GlobalEvents.Instance.SolutionAltered.Subscribe(OnSolutionAltered);
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Subscribe(UpdateEditorThemeAsync);
		SetCodeRegionTags("#region", "#endregion");
		//AddGitGutter();
	}

	private readonly CancellationSeries _solutionAlteredCancellationTokenSeries = new();
	private async Task OnSolutionAltered()
	{
		try
		{
			using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnSolutionAltered)}");
			if (_currentFile is null) return;
			if (_fileDeleted) return;
			GD.Print($"[{_currentFile.Name}] Solution altered, updating project diagnostics for file");
			var newCt = _solutionAlteredCancellationTokenSeries.CreateNext();
			var hasFocus = this.InvokeAsync(HasFocus);
			var documentSyntaxHighlighting = _roslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile, newCt);
			var razorSyntaxHighlighting = _roslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile, newCt);
			await Task.WhenAll(documentSyntaxHighlighting, razorSyntaxHighlighting).WaitAsync(newCt);
			if (newCt.IsCancellationRequested) return;
			var documentDiagnosticsTask = _roslynAnalysis.GetDocumentDiagnostics(_currentFile, newCt);
			await this.InvokeAsync(async () => SetSyntaxHighlightingModel(await documentSyntaxHighlighting, await razorSyntaxHighlighting));
			var documentDiagnostics = await documentDiagnosticsTask;
			if (newCt.IsCancellationRequested) return;
			var documentAnalyzerDiagnosticsTask = _roslynAnalysis.GetDocumentAnalyzerDiagnostics(_currentFile, newCt);
			await this.InvokeAsync(() => SetDiagnostics(documentDiagnostics));
			var documentAnalyzerDiagnostics = await documentAnalyzerDiagnosticsTask;
			if (newCt.IsCancellationRequested) return;
			await this.InvokeAsync(() => SetAnalyzerDiagnostics(documentAnalyzerDiagnostics));
			if (newCt.IsCancellationRequested) return;
			if (await hasFocus)
			{
				await _roslynAnalysis.UpdateProjectDiagnosticsForFile(_currentFile, newCt);
				if (newCt.IsCancellationRequested) return;
			}
		}
		catch (Exception e) when (e is OperationCanceledException)
		{
			// Ignore
		}
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
		_projectDiagnosticsObserveDisposable?.Dispose();
		GlobalEvents.Instance.SolutionAltered.Unsubscribe(OnSolutionAltered);
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Unsubscribe(UpdateEditorThemeAsync);
		if (_currentFile is not null) _openTabsFileManager.CloseFile(_currentFile);
	}
	
	private void OnFocusEntered()
	{
		// The selected tab changed, report the caret position
		_editorCaretPositionService.CaretPosition = GetCaretPosition(startAt1: true);
	}

	private async void OnBreakpointToggled(long line)
	{
		if (_fileChangingSuppressBreakpointToggleEvent) return;
		var lineInt = (int)line;
		var breakpointAdded = IsLineBreakpointed(lineInt);
		var lineForDebugger = lineInt + 1; // Godot is 0-indexed, Debugging is 1-indexed
		if (breakpointAdded)
		{
			await _runService.AddBreakpointForFile(_currentFile, lineForDebugger);
		}
		else
		{
			await _runService.RemoveBreakpointForFile(_currentFile, lineForDebugger);
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
		var caretPosition = GetCaretPosition(startAt1: true);
		if (HasSelection())
		{
			_selectionChangedQueue.AddWork();
		}
		else
		{
			_editorCaretPositionService.SelectionInfo = null;
		}
		_editorCaretPositionService.CaretPosition = caretPosition;
	}

	private void OnTextChanged()
	{
		_ = Task.GodotRun(async () =>
		{
			var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnTextChanged)}");
			_currentFile.IsDirty.Value = true;
			await _fileChangedService.SharpIdeFileChanged(_currentFile, Text, FileChangeType.IdeUnsavedChange);
			if (pendingCompletionTrigger is not null)
			{
				var cursorPosition = GetCaretPosition();
				var linePosition = new LinePosition(cursorPosition.line, cursorPosition.col);
				completionTrigger = pendingCompletionTrigger;
				pendingCompletionTrigger = null;
				var shouldTriggerCompletion = await _roslynAnalysis.ShouldTriggerCompletionAsync(_currentFile, Text, linePosition, completionTrigger!.Value);
				GD.Print($"Code completion trigger typed: '{completionTrigger.Value.Character}' at {linePosition.Line}:{linePosition.Character} should trigger: {shouldTriggerCompletion}");
				if (shouldTriggerCompletion)
				{
					await OnCodeCompletionRequested(completionTrigger.Value);
				}
			}
			else if (pendingCompletionFilterReason is not null)
			{
				var filterReason = pendingCompletionFilterReason.Value;
				pendingCompletionFilterReason = null;
				await CustomFilterCodeCompletionCandidates(filterReason);
			}
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
			GrabFocus(true);
			AdjustViewportToCaret();
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
		var project = ((IChildSharpIdeNode)_currentFile).GetNearestProjectNode();
		if (project is not null)
		{
			_projectDiagnosticsObserveDisposable = project.Diagnostics.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (innerEvent, ct) =>
				{
					var projectDiagnosticsForFile = project.Diagnostics.Where(s => s.FilePath == _currentFile.Path).ToImmutableArray();
					await this.InvokeAsync(() => SetProjectDiagnostics(projectDiagnosticsForFile));
				});
		}
		
		var syntaxHighlighting = _roslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
		var razorSyntaxHighlighting = _roslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile);
		var diagnostics = _roslynAnalysis.GetDocumentDiagnostics(_currentFile);
		var analyzerDiagnostics = _roslynAnalysis.GetDocumentAnalyzerDiagnostics(_currentFile);
		await readFileTask;
		var setTextTask = this.InvokeAsync(async () =>
		{
			_fileChangingSuppressBreakpointToggleEvent = true;
			SetText(await readFileTask);
			_fileChangingSuppressBreakpointToggleEvent = false;
			ClearUndoHistory();
			if (fileLinePosition is not null) SetFileLinePosition(fileLinePosition.Value);
		});
		_ = Task.GodotRun(async () =>
		{
			await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, setTextTask); // Text must be set before setting syntax highlighting
			await this.InvokeAsync(async () => SetSyntaxHighlightingModel(await syntaxHighlighting, await razorSyntaxHighlighting));
			await diagnostics;
			await this.InvokeAsync(async () => SetDiagnostics(await diagnostics));
			await analyzerDiagnostics;
			await this.InvokeAsync(async () => SetAnalyzerDiagnostics(await analyzerDiagnostics));
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

		RenderingServer.Singleton.DrawDashedLine(_aboveCanvasItemRid!.Value, startPos, endPos, color, thickness);
	}
	public override void _Draw()
	{
		RenderingServer.Singleton.CanvasItemClear(_aboveCanvasItemRid!.Value);
		//UnderlineRange(_currentLine, _selectionStartCol, _selectionEndCol, new Color(1, 0, 0));
		foreach (var sharpIdeDiagnostic in _fileDiagnostics.Concat(_fileAnalyzerDiagnostics).ConcatFast(_projectDiagnosticsForFile))
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
		DrawCompletionsPopup();
	}

	// This only gets invoked if the Node is focused
	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion) return;
		if (CompletionsPopupTryConsumeGuiInput(@event))
		{
			AcceptEvent();
			return;
		}
		if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } mouseEvent)
		{
			var (col, line) = GetLineColumnAtPos((Vector2I)mouseEvent.Position);
			var current = _navigationHistoryService.Current.Value;
			if (current!.File != _currentFile) throw new InvalidOperationException("Current navigation history file does not match the focused code editor file.");
			if (current.LinePosition.Line != line) // Only record a new navigation if the line has changed
			{
				_navigationHistoryService.RecordNavigation(_currentFile, new SharpIdeFileLinePosition(line, col));
			}
		}
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		CloseSymbolHoverWindow();
		// Let each open tab respond to this event
		if (@event.IsActionPressed(InputStringNames.SaveAllFiles))
		{
			AcceptEvent();
			_ = Task.GodotRun(async () =>
			{
				await _fileChangedService.SharpIdeFileChanged(_currentFile, Text, FileChangeType.IdeSaveToDisk);
			});
		}
		// Now we filter to only the focused tab
		if (HasFocus() is false) return;
		if (@event.IsActionPressed(InputStringNames.RenameSymbol))
		{
			_ = Task.GodotRun(async () => await RenameSymbol());
		}
		else if (@event.IsActionPressed(InputStringNames.CodeFixes))
		{
			EmitSignalCodeFixesRequested();
		}
		else if (@event.IsActionPressed(InputStringNames.SaveFile) && @event.IsActionPressed(InputStringNames.SaveAllFiles) is false)
		{
			AcceptEvent();
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
	private void SetAnalyzerDiagnostics(ImmutableArray<SharpIdeDiagnostic> diagnostics)
	{
		_fileAnalyzerDiagnostics = diagnostics;
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
			var codeActions = await _roslynAnalysis.GetCodeActionsForDocumentAtPosition(_currentFile, linePos);
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
	
	private (int line, int col) GetCaretPosition(bool startAt1 = false)
	{
		var caretColumn = GetCaretColumn();
		var caretLine = GetCaretLine();
		if (startAt1)
		{
			caretColumn += 1;
			caretLine += 1;
		}
		return (caretLine, caretColumn);
	}
}