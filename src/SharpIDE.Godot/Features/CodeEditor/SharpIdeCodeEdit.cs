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
	private Rid _canvasItemRid;
	private Window _completionDescriptionWindow = null!;
	private Window _methodSignatureHelpWindow = null!;
	private RichTextLabel _completionDescriptionLabel = null!;
	private FindReplaceBar _findReplaceBar = null!;

	private ImmutableArray<SharpIdeDiagnostic> _fileDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _fileAnalyzerDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _projectDiagnosticsForFile = [];
	private ImmutableArray<CodeAction> _currentCodeActionsInPopup = [];
	private bool _fileChangingSuppressBreakpointToggleEvent;
	private bool _settingWholeDocumentTextSuppressLineEditsEvent; // A dodgy workaround - setting the whole document doesn't guarantee that the line count stayed the same etc. We are still going to have broken highlighting. TODO: Investigate getting minimal text change ranges, and change those ranges only
	private bool _fileDeleted;
	// Captured in _GuiInput *before* a line-modifying keystroke is processed, so that OnLinesEditedFrom
	// can determine the correct LineEditOrigin from pre-edit state rather than post-edit state.
	private (int line, int col, string lineText)? _pendingLineEditOrigin;
	private IDisposable? _projectDiagnosticsObserveDisposable;
	private Color _cachedCurrentCaretLineColor;

	[Inject] private readonly IdeOpenTabsFileManager _openTabsFileManager = null!;
	[Inject] private readonly RunService _runService = null!;
	[Inject] private readonly RoslynAnalysis _roslynAnalysis = null!;
	[Inject] private readonly IdeCodeActionService _ideCodeActionService = null!;
	[Inject] private readonly FileChangedService _fileChangedService = null!;
	[Inject] private readonly IdeApplyCompletionService _ideApplyCompletionService = null!;
	[Inject] private readonly IdeNavigationHistoryService _navigationHistoryService = null!;
	[Inject] private readonly EditorCaretPositionService _editorCaretPositionService = null!;
	[Inject] private readonly SharpIdeMetadataAsSourceService _sharpIdeMetadataAsSourceService = null!;

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
		_completionDescriptionWindow = GetNode<Window>("%CompletionDescriptionWindow");
		_methodSignatureHelpWindow = GetNode<Window>("%MethodSignatureHelpWindow");
		_completionDescriptionLabel = _completionDescriptionWindow.GetNode<RichTextLabel>("PanelContainer/RichTextLabel");
		_canvasItemRid = GetCanvasItem();
		_cachedCurrentCaretLineColor = GetThemeColor(ThemeStringNames.CodeEdit.CurrentLineColor, GodotNodeStringNames.CodeEdit);
		RenderingServer.Singleton.CanvasItemSetParent(_aboveCanvasItemRid.Value, _canvasItemRid);
		_findReplaceBar = GetNode<FindReplaceBar>("%FindReplaceBar");
		_findReplaceBar.SetTextEdit(this);
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
		//AddGitGutter();
		var hScrollBar = GetHScrollBar();
		var vScrollBar = GetVScrollBar();
		hScrollBar.ValueChanged += OnCodeEditScrolled;
		vScrollBar.ValueChanged += OnCodeEditScrolled;
		AddCommentDelimiter("#","", true);
		SetCodeRegionTags("region", "endregion");
		GodotGlobalEvents.Instance.TextEditorCodeFoldingChanged.Subscribe(SetCodeFoldingAsync);
		SetCodeFolding(Singletons.AppState.IdeSettings.EditorEnableFolding);
	}
	
	public override void _Notification(int what)
	{
		if (what == NotificationThemeChanged) 
			_cachedCurrentCaretLineColor = GetThemeColor(ThemeStringNames.CodeEdit.CurrentLineColor, GodotNodeStringNames.CodeEdit);
	}

	private async Task SetCodeFoldingAsync(bool enabled) => await this.InvokeAsync(() => SetCodeFolding(enabled));
	[RequiresGodotUiThread]
	private void SetCodeFolding(bool enabled)
	{
		LineFolding = enabled;
		GuttersDrawFoldGutter = enabled;
	}

	private readonly CancellationSeries _solutionAlteredCancellationTokenSeries = new();
	private async Task OnSolutionAltered()
	{
		try
		{
			using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnSolutionAltered)}");
			if (_currentFile is null) return;
			if (_fileDeleted) return;
			GD.Print($"[{_currentFile.Name.Value}] Solution altered, updating project diagnostics for file");
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
		MidLine,
		EndOfLine,
		Unknown
	}
	// Line removed - fromLine 55, toLine 54
	// Line added - fromLine 54, toLine 55
	// Multi cursor gets a single line event for each
	private void OnLinesEditedFrom(long fromLine, long toLine)
	{
		if (fromLine == toLine) return;
		if (_settingWholeDocumentTextSuppressLineEditsEvent) return;

		// Consume the pre-edit snapshot captured in _GuiInput (if any).
		// Because the snapshot was taken *before* the edit, the caret position and line text
		// are exactly what they were at the moment the key was pressed — no post-edit guesswork.
		var snapshot = _pendingLineEditOrigin;
		_pendingLineEditOrigin = null;

		var origin = LineEditOrigin.Unknown;
		if (snapshot is not null)
		{
			var (_, snapCol, snapText) = snapshot.Value;
			var clampedCol = Math.Min(snapCol, snapText.Length);
			var textBeforeCaret = snapText.AsSpan()[..clampedCol];
			var textAfterCaret  = snapText.AsSpan()[clampedCol..];

			if (textBeforeCaret.IsEmpty || textBeforeCaret.IsWhiteSpace())
				origin = LineEditOrigin.StartOfLine;
			else if (textAfterCaret.IsEmpty || textAfterCaret.IsWhiteSpace())
				origin = LineEditOrigin.EndOfLine;
			else
				origin = LineEditOrigin.MidLine;
		}

		//GD.Print($"Lines edited from {fromLine} to {toLine}, origin: {origin}");
		_syntaxHighlighter.LinesChanged(fromLine, toLine, origin);
	}

	public override void _ExitTree()
	{
		_currentFile?.FileContentsChangedExternally.Unsubscribe(OnFileChangedExternally);
		_currentFile?.FileDeleted.Unsubscribe(OnFileDeleted);
		_projectDiagnosticsObserveDisposable?.Dispose();
		GlobalEvents.Instance.SolutionAltered.Unsubscribe(OnSolutionAltered);
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Unsubscribe(UpdateEditorThemeAsync);
		GodotGlobalEvents.Instance.TextEditorCodeFoldingChanged.Unsubscribe(SetCodeFoldingAsync);
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
		_findReplaceBar.LineColChangedForResult = false;
	}

	private void OnTextChanged()
	{
		_findReplaceBar.NeedsToCountResults = true;
		var text = Text;
		var pendingCompletionTrigger = _pendingCompletionTrigger;
		_pendingCompletionTrigger = null;
		var cursorPosition = GetCaretPosition();
		_ = Task.GodotRun(async () =>
		{
			var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnTextChanged)}");
			_currentFile.IsDirty.Value = true;
			await _fileChangedService.SharpIdeFileChanged(_currentFile, text, FileChangeType.IdeUnsavedChange);
			if (pendingCompletionTrigger is not null)
			{
				_completionTrigger = pendingCompletionTrigger;
				var linePosition = new LinePosition(cursorPosition.line, cursorPosition.col);
				var shouldTriggerCompletion = await _roslynAnalysis.ShouldTriggerCompletionAsync(_currentFile, text, linePosition, _completionTrigger!.Value);
				GD.Print($"Code completion trigger typed: '{_completionTrigger.Value.Character}' at {linePosition.Line}:{linePosition.Character} should trigger: {shouldTriggerCompletion}");
				if (shouldTriggerCompletion)
				{
					await OnCodeCompletionRequested(_completionTrigger.Value, text, cursorPosition);
				}
			}
			else if (_pendingCompletionFilterReason is not null)
			{
				var filterReason = _pendingCompletionFilterReason.Value;
				_pendingCompletionFilterReason = null;
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
			var (firstVisibleLine, lastFullVisibleLine) = (GetFirstVisibleLine(), GetLastFullVisibleLine());
			var caretLine = GetCaretLine();
			if (caretLine < firstVisibleLine || caretLine > lastFullVisibleLine)
			{
				CenterViewportToCaret();
			}
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
		if (_currentFile.GetContainingProjectFolder() is {} containingProjectFolder && Solution!.GetProjectForContainingFolderPath(containingProjectFolder) is {} project)
		{
			_projectDiagnosticsObserveDisposable = project.Diagnostics.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (innerEvent, ct) =>
				{
					var projectDiagnosticsForFile = project.Diagnostics.Where(s => s.FilePath == _currentFile.Path).ToImmutableArray();
					await this.InvokeAsync(() => SetProjectDiagnostics(projectDiagnosticsForFile));
				}, configureAwait: false);
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
			if (file.IsMetadataAsSourceFile) Editable = false;
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
	public void DrawLineBackgroundColorCustom(int line, Color color)
	{
		var startRect = GetRectAtLineColumn(line, 0);
		if (startRect.Position.X < 0) return; // line is off-screen
		var rect = new Rect2(
			0,
			startRect.Position.Y,
			Size.X - (MinimapDraw ? MinimapWidth : 0),
			GetLineHeight()
		);
		RenderingServer.Singleton.CanvasItemAddRect(_canvasItemRid, rect, color);
	}
	public override void _Draw()
	{
		RenderingServer.Singleton.CanvasItemClear(_aboveCanvasItemRid!.Value);
		
		// Draw a guideline at the left edge of the text area
		var gutterWidth = GetTotalGutterWidth();
		var leftEdgeStart = new Vector2(gutterWidth, 0);
		var leftEdgeEnd = new Vector2(gutterWidth, Size.Y);
		RenderingServer.Singleton.CanvasItemAddLine(_aboveCanvasItemRid.Value, leftEdgeStart, leftEdgeEnd, new Color("464646"), 1);

		var currentCaretLine = GetCaretLine();
		// We draw this ourselves, as the normal "current caret line bg color" is placed above anything drawn in _Draw, so we can't draw e.g. 'Executing line col range bg highlights' above caret line bg, but below text
		DrawLineBackgroundColorCustom(currentCaretLine, _cachedCurrentCaretLineColor);
		
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

		// Capture pre-edit caret state for line-modifying keystrokes, so that OnLinesEditedFrom
		// can determine LineEditOrigin from the state *before* the edit happened.
		// We only do this for single-caret edits; multi-caret falls back to Unknown.
		if (@event is InputEventKey { Pressed: true } keyEvent && GetCaretCount() == 1)
		{
			var (caretLine, caretCol) = GetCaretPosition();
			switch (keyEvent.Keycode)
			{
				// Enter / numpad Enter — line(s) added
				case Key.Enter or Key.KpEnter:
					_pendingLineEditOrigin = (caretLine, caretCol, GetLine(caretLine));
					break;
				// Forward-delete at end of line merges the next line up — line removed
				case Key.Delete when !HasSelection():
				{
					var lineText = GetLine(caretLine);
					if (caretCol == lineText.Length && caretLine < GetLineCount() - 1)
						_pendingLineEditOrigin = (caretLine, caretCol, lineText);
					break;
				}
			}
		}

		if (@event.IsActionPressed(InputStringNames.Backspace, true) && HasSelection() is false)
		{
			var (caretLine, caretCol) = GetCaretPosition();
			if (caretLine > 0 && caretCol > 0)
			{
				var lineText = GetLine(caretLine); // I do not like allocating every time backspace is pressed
				var textBeforeCaret = lineText.AsSpan()[..caretCol];
				if (textBeforeCaret.IsEmpty || textBeforeCaret.IsWhiteSpace())
				{
					// Capture pre-edit state before RemoveText triggers LinesEditedFrom
					if (GetCaretCount() == 1) _pendingLineEditOrigin = (caretLine, caretCol, lineText);
					BeginComplexOperation();
					var prevLine = caretLine - 1;
					var prevLineLength = GetLine(prevLine).Length;
					RemoveText(fromLine: prevLine, fromColumn: prevLineLength, toLine: caretLine, toColumn: caretCol);
					SetCaretLine(prevLine);
					SetCaretColumn(prevLineLength);
					EndComplexOperation();
					ResetCompletionPopupState();
					AcceptEvent();
					return;
				}
			}
		}
		if (@event.IsActionPressed(InputStringNames.CodeEditorDuplicateLine))
		{
			DuplicateSelection();
			return;
		}
		if (MethodSignatureHelpPopupTryConsumeGuiInput(@event))
		{
			AcceptEvent();
			return;
		}
		if (CompletionsPopupTryConsumeGuiInput(@event))
		{
			AcceptEvent();
			return;
		}
		if (@event.IsActionPressed(InputStringNames.CodeEditorRemoveLine))
		{
			DeleteLines();
			return;
		}
		if (@event.IsActionPressed(InputStringNames.CodeEditorMoveLineUp))
		{
			MoveLinesUp();
			return;
		}
		if (@event.IsActionPressed(InputStringNames.CodeEditorMoveLineDown))
		{
			MoveLinesDown();
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

		if (@event.IsActionPressed(InputStringNames.FindInCurrentFile, exactMatch: true))
		{
			AcceptEvent();
			_findReplaceBar.PopupSearch();
		}
		else if (@event.IsActionPressed(InputStringNames.ReplaceInCurrentFile))
		{
			AcceptEvent();
			_findReplaceBar.PopupReplace();
		}
		else if (@event.IsActionPressed(InputStringNames.RenameSymbol))
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
