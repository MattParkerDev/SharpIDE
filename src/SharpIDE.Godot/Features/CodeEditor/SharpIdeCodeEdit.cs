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
	private SharpIdeProjectModel? _project;

	private CustomHighlighter _syntaxHighlighter = new();
	private PopupMenu _popupMenu = null!;
	private CanvasItem _aboveCanvasItem = null!;
	private Rid? _aboveCanvasItemRid = null!;
	private Rid _canvasItemRid;
	private Window _completionDescriptionWindow = null!;
	private Window _methodSignatureHelpWindow = null!;
	private RichTextLabel _completionDescriptionLabel = null!;
	private FindReplaceBar _findReplaceBar = null!;
	private OptionButton _tfmOptionButton = null!;

	private ImmutableArray<SharpIdeDiagnostic> _fileDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _fileAnalyzerDiagnostics = [];
	private ImmutableArray<SharpIdeDiagnostic> _projectDiagnosticsForFile = [];
	private ImmutableArray<CodeAction> _currentCodeActionsInPopup = [];
	private bool _fileChangingSuppressBreakpointToggleEvent;
	private bool _fileDeleted;
	// The editor owns an open file's text. These mirror Text (UI thread only): _currentSourceText is built from successive WithChanges,
	// so Roslyn can reparse incrementally, and so edits computed against an older text can be mapped forward.
	// _textMirror holds Godot's native copy of the previous text, so each edit is found without allocating the whole document as a managed string.
	private readonly TextEditNativeTextMirror _textMirror = new();
	private SourceText? _currentSourceText;
	private bool _pendingReloadMarksClean; // The next text change is a reload from disk, so leaves the file not dirty
	// Captured in _GuiInput *before* a line-modifying keystroke is processed, so that OnLinesEditedFrom
	// can determine the correct LineEditOrigin from pre-edit state rather than post-edit state.
	private (int line, int col, string lineText)? _pendingLineEditOrigin;
	private IDisposable? _projectDiagnosticsObserveDisposable;
	private Color _cachedCurrentCaretLineColor;
	private readonly Color _breakpointLineColor = new Color("3a2323");
	private readonly Color _executingLineColor = new Color("665001");
	private LinePositionSpan? _executingLineInfo; // Used for drawing orange executing background highlighting
	// Hidden tabs don't re-analyse on SolutionAltered; they set this and refresh when they become visible. Only read/written on the UI thread.
	private bool _analysisStale;
	// The workspace version the current highlighting/diagnostics were computed against. If unchanged, a refresh would produce identical results.
	// Written from the thread pool by overlapping refreshes, and a nullable struct this size isn't written atomically, hence the lock.
	private VersionStamp? _lastAnalysedVersion;
	private readonly Lock _lastAnalysedVersionLock = new();
	private VersionStamp? LastAnalysedVersion
	{
		get { lock (_lastAnalysedVersionLock) return _lastAnalysedVersion; }
		set { lock (_lastAnalysedVersionLock) _lastAnalysedVersion = value; }
	}

	[Inject] private readonly IdeOpenTabsFileManager _openTabsFileManager = null!;
	[Inject] private readonly RunService _runService = null!;
	[Inject] private readonly RoslynAnalysis _roslynAnalysis = null!;
	[Inject] private readonly IdeCodeActionService _ideCodeActionService = null!;
	[Inject] private readonly FileChangedService _fileChangedService = null!;
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
		_tfmOptionButton = GetNode<OptionButton>("%TfmOptionButton");
		_tfmOptionButton.ItemSelected += OnTargetFrameworkSelected;
		_completionDescriptionWindow = GetNode<Window>("%CompletionDescriptionWindow");
		_methodSignatureHelpWindow = GetNode<Window>("%MethodSignatureHelpWindow");
		_completionDescriptionLabel = _completionDescriptionWindow.GetNode<RichTextLabel>("PanelContainer/RichTextLabel");
		_canvasItemRid = GetCanvasItem();
		_cachedCurrentCaretLineColor = GetThemeColor(ThemeStringNames.CodeEdit.CurrentLineColor, GodotNodeStringNames.CodeEdit);
		RenderingServer.Singleton.CanvasItemSetParent(_aboveCanvasItemRid.Value, _canvasItemRid);
		InitializeCompletionPopup();
		_symbolHoverPopup = new SymbolHoverPopup(this);
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
		VisibilityChanged += OnVisibilityChanged;
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
		{
			_cachedCurrentCaretLineColor = GetThemeColor(ThemeStringNames.CodeEdit.CurrentLineColor, GodotNodeStringNames.CodeEdit);
			CallDeferred(CanvasItem.MethodName.QueueRedraw); // To Redraw diagnostics etc - without this, they disappear on a theme change, until the next redraw (click, scroll etc)
		}
	}

	private async Task SetCodeFoldingAsync(bool enabled) => await this.InvokeAsync(() => SetCodeFolding(enabled));
	[RequiresGodotUiThread]
	private void SetCodeFolding(bool enabled)
	{
		LineFolding = enabled;
		GuttersDrawFoldGutter = enabled;
	}

	[RequiresGodotUiThread]
	private void OnVisibilityChanged()
	{
		if (_analysisStale is false || IsVisibleInTree() is false) return;
		_analysisStale = false;
		_ = Task.GodotRun(OnSolutionAltered);
	}

	private readonly CancellationSeries _solutionAlteredCancellationTokenSeries = new();
	private async Task OnSolutionAltered()
	{
		try
		{
			using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnSolutionAltered)}");
			if (_currentFile is null) return;
			if (_fileDeleted) return;
			var newCt = _solutionAlteredCancellationTokenSeries.CreateNext(); // Also cancels any in-flight refresh, including one for a tab that has since been hidden
			// Check visibility and mark stale in one UI-thread step, so we can't race with OnVisibilityChanged
			var (isVisible, hasFocus) = await this.InvokeAsync(() =>
			{
				var visible = IsVisibleInTree();
				if (visible is false) _analysisStale = true;
				return (visible, HasFocus());
			});
			if (isVisible is false) return; // Refreshed in OnVisibilityChanged when the tab is shown
			var analysisVersion = await _roslynAnalysis.GetFileAnalysisVersion(_currentFile, newCt);
			if (analysisVersion is not null && analysisVersion == LastAnalysedVersion) return; // Nothing this file depends on has changed
			GD.Print($"[{_currentFile.Name.Value}] Solution altered, updating project diagnostics for file");
			var syntaxHighlighting = await _roslynAnalysis.GetSyntaxHighlighting(_currentFile, newCt).WaitAsync(newCt);
			if (newCt.IsCancellationRequested) return;
			var documentDiagnosticsTask = _roslynAnalysis.GetDocumentDiagnostics(_currentFile, newCt);
			await this.InvokeAsync(() => SetSyntaxHighlightingModel(syntaxHighlighting));
			var documentDiagnostics = await documentDiagnosticsTask;
			if (newCt.IsCancellationRequested) return;
			var documentAnalyzerDiagnosticsTask = _roslynAnalysis.GetDocumentAnalyzerDiagnostics(_currentFile, newCt);
			await this.InvokeAsync(() => SetDiagnostics(documentDiagnostics));
			var documentAnalyzerDiagnostics = await documentAnalyzerDiagnosticsTask;
			if (newCt.IsCancellationRequested) return;
			await this.InvokeAsync(() => SetAnalyzerDiagnostics(documentAnalyzerDiagnostics));
			if (newCt.IsCancellationRequested) return;
			if (hasFocus)
			{
				await _roslynAnalysis.UpdateProjectDiagnosticsForFile(_currentFile, newCt);
				if (newCt.IsCancellationRequested) return;
			}
			LastAnalysedVersion = analysisVersion;
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
		_currentFile?.EditRequested.Unsubscribe(OnEditRequested);
		_currentFile?.FileDeleted.Unsubscribe(OnFileDeleted);
		_projectDiagnosticsObserveDisposable?.Dispose();
		GlobalEvents.Instance.SolutionAltered.Unsubscribe(OnSolutionAltered);
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Unsubscribe(UpdateEditorThemeAsync);
		GodotGlobalEvents.Instance.TextEditorCodeFoldingChanged.Unsubscribe(SetCodeFoldingAsync);
		_tfmOptionButton.ItemSelected -= OnTargetFrameworkSelected;
		if (_currentFile is not null)
		{
			if (_fileDeleted is false) SyncTextFromEditor(); // Capture an edit whose text_changed hasn't been handled yet
			// Saves unsaved changes, so Roslyn (which has the editor's text) and disk agree once the editor is gone
			_ = _openTabsFileManager.CloseFileAsync(_currentFile, saveIfDirty: _fileDeleted is false);
		}
		_textMirror.Dispose();
		_completionPopup.Dispose();
		_symbolHoverPopup.Close();
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
		var pendingCompletionTrigger = _completionPopup.TakePendingCompletionTrigger();
		var pendingCompletionFilterReason = _completionPopup.TakePendingFilterReason();
		// May find no change, if a programmatic edit already synced it
		SyncTextFromEditor();
		if (pendingCompletionTrigger is null && pendingCompletionFilterReason is null) return;
		if (_currentSourceText is not { } text) return;
		var cursorPosition = GetCaretPosition();
		_ = Task.GodotRun(async () =>
		{
			var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnTextChanged)}");
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
			else if (pendingCompletionFilterReason is not null)
			{
				await CustomFilterCodeCompletionCandidates(pendingCompletionFilterReason.Value);
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

	/// Brings _currentSourceText up to date with Text, and reports the change. No managed allocation of the document - Godot's native text is diffed directly.
	/// Called from text_changed, and before/after programmatic edits, so they never work against a stale base.
	/// Returns true if the text had changed.
	[RequiresGodotUiThread]
	private bool SyncTextFromEditor()
	{
		if (_currentSourceText is null) return false; // Not loaded yet
		if (_textMirror.Update(this) is not { } change) return false;
		_currentSourceText = _currentSourceText.WithChanges(change);
		VerifySourceTextMatchesEditor();
		var isDirty = _pendingReloadMarksClean is false;
		_pendingReloadMarksClean = false;
		// Cheap and synchronous - records the text and queues the Roslyn update, which happens in order, off the UI thread
		_fileChangedService.OpenFileTextChanged(_currentFile, _currentSourceText, isDirty);
		return true;
	}

	/// Catches the editor and Roslyn silently drifting apart, e.g. a UTF-32 -> UTF-16 offset bug. Skipped for large files, as it allocates the whole document.
	[System.Diagnostics.Conditional("DEBUG")]
	private void VerifySourceTextMatchesEditor()
	{
		if (_currentSourceText is null || _currentSourceText.Length > 200_000) return;
		if (_currentSourceText.ToString() != Text) GD.PrintErr($"[{_currentFile.Name.Value}] BUG: Roslyn's text no longer matches the editor's text");
	}

	private async Task OnEditRequested(SharpIdeFileEditRequest request)
	{
		if (_fileDeleted) return; // We have QueueFree'd this node, however it may not have been freed yet.
		await this.InvokeAsync(() => ApplyEditRequest(request));
	}

	[RequiresGodotUiThread]
	private void ApplyEditRequest(SharpIdeFileEditRequest request)
	{
		SyncTextFromEditor();
		if (_currentSourceText is null) return; // Not loaded yet
		if (request.ReloadedText is { } reloadedText)
		{
			// Disk wins. Applied as a minimal edit (like VS Code), so the caret, scroll position, breakpoints and undo history survive.
			// The editor may have normalised line endings, so compare like with like.
			var currentText = _currentSourceText.ToString(); // Reloads are rare, so allocating the document here is fine
			if (currentText.Contains('\r') is false) reloadedText = reloadedText.Replace("\r\n", "\n");
			if (TextDiff.GetMinimalChange(currentText, reloadedText) is not { } reloadChange)
			{
				_currentFile.IsDirty.Value = false;
				return;
			}
			_pendingReloadMarksClean = true;
			ApplyChanges(_currentSourceText, [reloadChange]);
			return;
		}

		if (ReferenceEquals(request.BaseText, _currentSourceText) is false && request.BaseText!.ContentEquals(_currentSourceText) is false)
		{
			GD.PrintErr($"[{_currentFile.Name.Value}] Edit not applied - the file has changed since it was computed");
			return;
		}
		ApplyChanges(_currentSourceText, request.Changes);
	}

	/// Applies changes computed against baseText, which must match the editor's current text, as one undo step.
	/// Godot keeps carets, breakpoints, folds etc in place around the edits, unlike replacing the whole text.
	[RequiresGodotUiThread]
	private void ApplyChanges(SourceText baseText, IReadOnlyList<TextChange> changes, int? newCaretOffset = null)
	{
		BeginComplexOperation();
		// Last to first, so each change's position in baseText is unaffected by those already applied
		foreach (var change in changes.OrderBy(c => c.Span.Start).Reverse())
		{
			var span = baseText.Lines.GetLinePositionSpan(change.Span);
			if (change.Span.IsEmpty is false) RemoveText(span.Start.Line, span.Start.Character, span.End.Line, span.End.Character);
			if (string.IsNullOrEmpty(change.NewText) is false) InsertText(change.NewText, span.Start.Line, span.Start.Character);
		}
		if (newCaretOffset is { } caretOffset)
		{
			var caretPosition = baseText.WithChanges(changes).Lines.GetLinePosition(caretOffset);
			RemoveSecondaryCarets();
			SetCaretLine(caretPosition.Line);
			SetCaretColumn(caretPosition.Character);
		}
		EndComplexOperation();
		// Report the result now, rather than waiting for text_changed
		SyncTextFromEditor();
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
		_currentFile.EditRequested.Subscribe(OnEditRequested);
		_currentFile.FileDeleted.Subscribe(OnFileDeleted);
		if (_currentFile.GetContainingProjectFolder() is {} containingProjectFolder && Solution!.GetProjectForContainingFolderPath(containingProjectFolder) is {} project)
		{
			_project = project;
			_projectDiagnosticsObserveDisposable = project.Diagnostics.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
				.Chunk(TimeSpan.FromMilliseconds(50)) // A diagnostics update raises a Remove and an Add - handle them together
				.SubscribeAwait(async (innerEvents, ct) =>
				{
					var projectDiagnosticsForFile = project.Diagnostics.Where(s => s.FilePath == _currentFile.Path).ToImmutableArray();
					// Most updates are for other files in the project - don't redraw this one if its diagnostics are unchanged.
					// Compared as sets, as hash set enumeration order can change after removals.
					var current = _projectDiagnosticsForFile;
					if (projectDiagnosticsForFile.Length == current.Length && current.ToHashSet().SetEquals(projectDiagnosticsForFile)) return;
					await this.InvokeAsync(() => SetProjectDiagnostics(projectDiagnosticsForFile));
				}, configureAwait: false);

			project.TfmSpecificLoadResults.SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (_, ct) => await this.InvokeAsync(UpdateTargetFrameworkOptions), configureAwait: false).AddToDeferred(this);
			project.ActiveMsBuildProjectLoadResult.SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (_, ct) => await this.InvokeAsync(UpdateSelectedTargetFramework), configureAwait: false).AddToDeferred(this);
		}

		// Captured before computing, so we never record a version newer than the one the results came from
		var analysisVersion = _roslynAnalysis.GetFileAnalysisVersion(_currentFile);
		var syntaxHighlighting = _roslynAnalysis.GetSyntaxHighlighting(_currentFile);
		var diagnostics = _roslynAnalysis.GetDocumentDiagnostics(_currentFile);
		var analyzerDiagnostics = _roslynAnalysis.GetDocumentAnalyzerDiagnostics(_currentFile);
		await readFileTask;
		var setTextTask = this.InvokeAsync(async () =>
		{
			_fileChangingSuppressBreakpointToggleEvent = true;
			SetText((await readFileTask).ToString());
			_fileChangingSuppressBreakpointToggleEvent = false;
			ClearUndoHistory();
			// The editor now owns this file's text. Godot may normalise it (e.g. line endings), so Roslyn is given exactly what the editor holds.
			_currentSourceText = SourceText.From(_textMirror.Reset(this));
			_fileChangedService.OpenFileTextChanged(file, _currentSourceText, isDirty: false);
			if (fileLinePosition is not null) SetFileLinePosition(fileLinePosition.Value);
			if (file.IsMetadataAsSourceFile) Editable = false;
		});
		_ = Task.GodotRun(async () =>
		{
			await Task.WhenAll(syntaxHighlighting, setTextTask); // Text must be set before setting syntax highlighting
			await this.InvokeAsync(async () => SetSyntaxHighlightingModel(await syntaxHighlighting));
			await diagnostics;
			await this.InvokeAsync(async () => SetDiagnostics(await diagnostics));
			await analyzerDiagnostics;
			await this.InvokeAsync(async () => SetAnalyzerDiagnostics(await analyzerDiagnostics));
			LastAnalysedVersion = await analysisVersion;
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
		int lineLength = TextEditNativeLine.GetLength(this, line);
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
	public void HighlightLineTextRange(int line, int startCol, int endCol, Color color)
	{
		if (line < 0 || line >= GetLineCount()) return;
		if (startCol > endCol) return;
		var lineLength = TextEditNativeLine.GetLength(this, line);
		startCol = Mathf.Clamp(startCol, 0, lineLength);
		endCol   = Mathf.Clamp(endCol,   0, lineLength);
		var startRect = GetRectAtLineColumn(line, startCol);
		var endRect   = GetRectAtLineColumn(line, endCol);
		float left  = startCol == 0 ? startRect.Position.X : startRect.End.X;
		float right = startCol == endCol ? endRect.End.X + 10 : endRect.End.X;
		float top   = startRect.Position.Y;
		float height = startRect.Size.Y;
		var rect = new Rect2(left, top, right - left, height);
		RenderingServer.Singleton.CanvasItemAddRect(_canvasItemRid, rect, color);
	}

	public void DrawLineBackgroundColorCustom(int line, Color color, bool includeLeftGutter = true)
	{
		var lineHeight = GetLineHeight();
		var vScroll = GetVScroll();
		var wholeVScroll = Math.Floor(vScroll);
		var styleBoxName = Editable ? "normal" : "read_only";
		var topMargin = GetThemeStylebox(styleBoxName, GodotNodeStringNames.CodeEdit).GetMargin(Side.Top);
		var y = topMargin
			+ (float)(GetScrollPosForLine(line) - wholeVScroll) * lineHeight
			- Mathf.Round((float)(vScroll - wholeVScroll) * lineHeight);
		if (y + lineHeight <= 0 || y >= Size.Y) return;
		var x = includeLeftGutter ? 0 : GetTotalGutterWidth();
		var rect = new Rect2(x, y, Size.X - (MinimapDraw ? MinimapWidth : 0), lineHeight);
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

		using var nativeInt32ArrayHandle = GetBreakpointedLinesUnmanaged();
		foreach (var line in nativeInt32ArrayHandle.Span)
		{
			if (line == currentCaretLine) continue;
			DrawLineBackgroundColorCustom(line, _breakpointLineColor, false);
		}
		// We draw this ourselves, as the normal "current caret line bg color" is placed above anything drawn in _Draw, so we can't draw e.g. 'Executing line col range bg highlights' above caret line bg, but below text
		DrawLineBackgroundColorCustom(currentCaretLine, _cachedCurrentCaretLineColor);

		// _Draw runs on every caret move/blink, scroll and keystroke - only do per-line work for lines that can be on screen.
		// The last visible line can be partially visible, hence +1. Lines hidden by folding inside this range are still skipped by the rect lookups.
		var firstVisibleLine = GetFirstVisibleLine();
		var lastVisibleLine = GetLastFullVisibleLine() + 1;

		if (_executingLineInfo is {} linePositionSpan)
		{
			var fromLine = Math.Max(linePositionSpan.Start.Line, firstVisibleLine);
			var toLine = Math.Min(linePositionSpan.End.Line, lastVisibleLine);
			for (var line = fromLine; line <= toLine; line++)
			{
				var lineStartCol = line == linePositionSpan.Start.Line ? linePositionSpan.Start.Character : 0;
				var lineEndCol = line == linePositionSpan.End.Line ? linePositionSpan.End.Character : int.MaxValue; // Clamped to the line length
				HighlightLineTextRange(line, lineStartCol, lineEndCol, _executingLineColor);
			}
		}

		UnderlineVisibleDiagnostics(_fileDiagnostics, firstVisibleLine, lastVisibleLine);
		UnderlineVisibleDiagnostics(_fileAnalyzerDiagnostics, firstVisibleLine, lastVisibleLine);
		UnderlineVisibleDiagnostics(_projectDiagnosticsForFile, firstVisibleLine, lastVisibleLine);
		DrawCompletionsPopup();
	}

	private static readonly Color DiagnosticErrorColor = new(1, 0, 0);
	private static readonly Color DiagnosticWarningColor = new("ffb700");
	private static readonly Color DiagnosticOtherColor = new(0, 1, 0);

	private void UnderlineVisibleDiagnostics(ImmutableArray<SharpIdeDiagnostic> diagnostics, int firstVisibleLine, int lastVisibleLine)
	{
		foreach (var sharpIdeDiagnostic in diagnostics)
		{
			var line = sharpIdeDiagnostic.Span.Start.Line;
			if (line < firstVisibleLine || line > lastVisibleLine) continue;
			var startCol = sharpIdeDiagnostic.Span.Start.Character;
			var endCol = sharpIdeDiagnostic.Span.End.Character;
			var color = sharpIdeDiagnostic.Severity switch
			{
				DiagnosticSeverity.Error => DiagnosticErrorColor,
				DiagnosticSeverity.Warning => DiagnosticWarningColor,
				_ => DiagnosticOtherColor // Info or other
			};
			UnderlineRange(line, startCol, endCol, color);
		}
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
		if (@event.IsActionPressed(InputStringNames.Copy))
		{
			if (_symbolHoverPopup.TryCopySelectedText())
			{
				AcceptEvent();
			}
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
		if (@event.IsActionPressed(InputStringNames.RenameSymbol))
		{
			AcceptEvent();
			_ = Task.GodotRun(async () => await RenameSymbol());
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
		if (@event is not InputEventKey { Keycode: Key.Ctrl or Key.Alt or Key.Shift or Key.Meta } && @event.IsActionPressed(InputStringNames.Copy) is false && @event.IsActionReleased(InputStringNames.Copy) is false)
		{
			CloseSymbolHoverWindow();
		}
		// Let each open tab respond to this event
		if (@event.IsActionPressed(InputStringNames.SaveAllFiles))
		{
			AcceptEvent();
			SaveFile();
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
		else if (@event.IsActionPressed(InputStringNames.CodeFixes))
		{
			EmitSignalCodeFixesRequested();
		}
		else if (@event.IsActionPressed(InputStringNames.SaveFile) && @event.IsActionPressed(InputStringNames.SaveAllFiles) is false)
		{
			AcceptEvent();
			SaveFile();
		}
	}

	[RequiresGodotUiThread]
	private void SaveFile()
	{
		SyncTextFromEditor(); // So the save includes an edit whose text_changed hasn't been handled yet
		_ = Task.GodotRun(async () => await _fileChangedService.SaveOpenFile(_currentFile));
	}

	public void SetExecutingTextSpanInfo(LinePositionSpan? linePositionSpan)
	{
		_executingLineInfo = linePositionSpan;
		QueueRedraw();
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
	private void SetSyntaxHighlightingModel(ImmutableArray<SharpIdeClassifiedSpan> classifiedSpans)
	{
		_syntaxHighlighter.SetHighlightingData(classifiedSpans);
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
