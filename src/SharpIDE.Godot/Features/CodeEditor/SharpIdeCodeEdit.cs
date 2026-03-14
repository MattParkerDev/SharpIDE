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
using SharpIDE.Application.Features.Git;
using SharpIDE.Application.Features.NavigationHistory;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Git;
using Task = System.Threading.Tasks.Task;

namespace SharpIDE.Godot.Features.CodeEditor;

#pragma warning disable VSTHRD101
public partial class SharpIdeCodeEdit : CodeEdit
{
	private static readonly Task<ImmutableArray<SharpIdeClassifiedSpan>> EmptyClassifiedSpansTask = Task.FromResult(ImmutableArray<SharpIdeClassifiedSpan>.Empty);
	private static readonly Task<ImmutableArray<SharpIdeRazorClassifiedSpan>> EmptyRazorClassifiedSpansTask = Task.FromResult(ImmutableArray<SharpIdeRazorClassifiedSpan>.Empty);
	private static readonly Task<ImmutableArray<SharpIdeDiagnostic>> EmptyDiagnosticsTask = Task.FromResult(ImmutableArray<SharpIdeDiagnostic>.Empty);
	private const int BehindInlineCanvasItemDrawIndex = 0;
	private const int AboveCanvasItemDrawIndex = 100;
	private const float AboveTextInlineHighlightOpacityScale = 0.42f;

	[Signal]
	public delegate void CodeFixesRequestedEventHandler();
	
	public SharpIdeSolutionModel? Solution { get; set; }
	public SharpIdeFile SharpIdeFile => _currentFile;
	private SharpIdeFile _currentFile = null!;
	
	private CustomHighlighter _syntaxHighlighter = new();
	private PopupMenu _popupMenu = null!;
	private Rid? _behindInlineCanvasItemRid = null!;
	private Rid? _aboveCanvasItemRid = null!;
	private Window _completionDescriptionWindow = null!;
	private Window _methodSignatureHelpWindow = null!;
	private RichTextLabel _completionDescriptionLabel = null!;
	private GitChangeScrollbarOverlay _gitChangeScrollbarOverlay = null!;
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
	private bool _ownsFileLifecycle = true;
	private bool _isPreviewMode;
	private readonly Dictionary<int, Color> _gitDiffLineBackgrounds = [];
	private readonly Dictionary<int, IReadOnlyList<GitDiffInlineDecoration>> _gitDiffInlineHighlights = [];
	private GitDiffTraceOperation? _pendingGitDiffTraceOperation;
	private GitDiffTraceRedrawTarget _pendingGitDiffTraceTarget;
	private long _fileContextVersion;
	
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
		_behindInlineCanvasItemRid = CreateOverlayCanvasItem(BehindInlineCanvasItemDrawIndex);
		_aboveCanvasItemRid = CreateOverlayCanvasItem(AboveCanvasItemDrawIndex);
		_completionDescriptionWindow = GetNode<Window>("%CompletionDescriptionWindow");
		_methodSignatureHelpWindow = GetNode<Window>("%MethodSignatureHelpWindow");
		_completionDescriptionLabel = _completionDescriptionWindow.GetNode<RichTextLabel>("PanelContainer/RichTextLabel");
		_gitChangeScrollbarOverlay = GetNode<GitChangeScrollbarOverlay>("%GitChangeScrollbarOverlay");
		_gitChangeScrollbarOverlay.Bind(this);
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
	}

	private readonly CancellationSeries _solutionAlteredCancellationTokenSeries = new();
	private async Task OnSolutionAltered()
	{
		try
		{
			using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(OnSolutionAltered)}");
			if (_currentFile is null) return;
			if (_fileDeleted) return;
			if (HasRoslynProjectContext(_currentFile) is false) return;
			var currentFile = _currentFile;
			var fileContextVersion = ReadFileContextVersion();
			GD.Print($"[{_currentFile.Name.Value}] Solution altered, updating project diagnostics for file");
			var newCt = _solutionAlteredCancellationTokenSeries.CreateNext();
			var hasFocus = this.InvokeAsync(HasFocus);
			var documentSyntaxHighlighting = _roslynAnalysis.GetDocumentSyntaxHighlighting(currentFile, newCt);
			var razorSyntaxHighlighting = _roslynAnalysis.GetRazorDocumentSyntaxHighlighting(currentFile, newCt);
			await Task.WhenAll(documentSyntaxHighlighting, razorSyntaxHighlighting).WaitAsync(newCt);
			if (newCt.IsCancellationRequested || !IsCurrentFileContext(currentFile, fileContextVersion)) return;
			var documentDiagnosticsTask = _roslynAnalysis.GetDocumentDiagnostics(currentFile, newCt);
			await this.InvokeAsync(async () =>
			{
				if (!IsCurrentFileContext(currentFile, fileContextVersion)) return;
				SetSyntaxHighlightingModel(await documentSyntaxHighlighting, await razorSyntaxHighlighting);
			});
			var documentDiagnostics = await documentDiagnosticsTask;
			if (newCt.IsCancellationRequested || !IsCurrentFileContext(currentFile, fileContextVersion)) return;
			var documentAnalyzerDiagnosticsTask = _roslynAnalysis.GetDocumentAnalyzerDiagnostics(currentFile, newCt);
			await this.InvokeAsync(() =>
			{
				if (!IsCurrentFileContext(currentFile, fileContextVersion)) return;
				SetDiagnostics(documentDiagnostics);
			});
			var documentAnalyzerDiagnostics = await documentAnalyzerDiagnosticsTask;
			if (newCt.IsCancellationRequested || !IsCurrentFileContext(currentFile, fileContextVersion)) return;
			await this.InvokeAsync(() =>
			{
				if (!IsCurrentFileContext(currentFile, fileContextVersion)) return;
				SetAnalyzerDiagnostics(documentAnalyzerDiagnostics);
			});
			if (newCt.IsCancellationRequested || !IsCurrentFileContext(currentFile, fileContextVersion)) return;
			if (await hasFocus)
			{
				await _roslynAnalysis.UpdateProjectDiagnosticsForFile(currentFile, newCt);
				if (newCt.IsCancellationRequested) return;
			}
		}
		catch (Exception e) when (e is OperationCanceledException)
		{
			// Ignore
		}
	}

	private static bool HasRoslynProjectContext(SharpIdeFile file)
	{
		return file.IsMetadataAsSourceFile || ((IChildSharpIdeNode)file).GetNearestProjectNode() is not null;
	}

	private Task<ImmutableArray<SharpIdeClassifiedSpan>> GetDocumentSyntaxHighlightingSafe(SharpIdeFile file)
	{
		return HasRoslynProjectContext(file) ? _roslynAnalysis.GetDocumentSyntaxHighlighting(file) : EmptyClassifiedSpansTask;
	}

	private Task<ImmutableArray<SharpIdeClassifiedSpan>> GetDocumentSyntaxHighlightingSafe(SharpIdeFile file, string previewText)
	{
		return HasRoslynProjectContext(file) ? _roslynAnalysis.GetDocumentSyntaxHighlightingForText(file, previewText) : EmptyClassifiedSpansTask;
	}

	private Task<ImmutableArray<SharpIdeRazorClassifiedSpan>> GetRazorDocumentSyntaxHighlightingSafe(SharpIdeFile file)
	{
		return HasRoslynProjectContext(file) ? _roslynAnalysis.GetRazorDocumentSyntaxHighlighting(file) : EmptyRazorClassifiedSpansTask;
	}

	private Task<ImmutableArray<SharpIdeDiagnostic>> GetDocumentDiagnosticsSafe(SharpIdeFile file)
	{
		return HasRoslynProjectContext(file) ? _roslynAnalysis.GetDocumentDiagnostics(file) : EmptyDiagnosticsTask;
	}

	private Task<ImmutableArray<SharpIdeDiagnostic>> GetDocumentAnalyzerDiagnosticsSafe(SharpIdeFile file)
	{
		return HasRoslynProjectContext(file) ? _roslynAnalysis.GetDocumentAnalyzerDiagnostics(file) : EmptyDiagnosticsTask;
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
		if (_ownsFileLifecycle)
		{
			_currentFile?.FileContentsChangedExternally.Unsubscribe(OnFileChangedExternally);
			_currentFile?.FileDeleted.Unsubscribe(OnFileDeleted);
		}
		_projectDiagnosticsObserveDisposable?.Dispose();
		GlobalEvents.Instance.SolutionAltered.Unsubscribe(OnSolutionAltered);
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Unsubscribe(UpdateEditorThemeAsync);
		FreeCanvasItemRid(ref _behindInlineCanvasItemRid);
		FreeCanvasItemRid(ref _aboveCanvasItemRid);
		if (_ownsFileLifecycle && _currentFile is not null) _openTabsFileManager.CloseFile(_currentFile);
	}

	private Rid CreateOverlayCanvasItem(int drawIndex)
	{
		var canvasItemRid = RenderingServer.Singleton.CanvasItemCreate();
		RenderingServer.Singleton.CanvasItemSetParent(canvasItemRid, GetCanvasItem());
		RenderingServer.Singleton.CanvasItemSetDrawIndex(canvasItemRid, drawIndex);
		return canvasItemRid;
	}

	private static void FreeCanvasItemRid(ref Rid? canvasItemRid)
	{
		if (canvasItemRid is not { } rid)
		{
			return;
		}

		RenderingServer.Singleton.FreeRid(rid);
		canvasItemRid = null;
	}
	
	private void OnFocusEntered()
	{
		if (_isPreviewMode) return;
		// The selected tab changed, report the caret position
		_editorCaretPositionService.CaretPosition = GetCaretPosition(startAt1: true);
	}

	private async void OnBreakpointToggled(long line)
	{
		if (_isPreviewMode) return;
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
		if (_isPreviewMode) return;
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
			_gitChangeScrollbarOverlay.RefreshLayout();
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

	public void NavigateToGitChange(int line)
	{
		var safeLine = Mathf.Clamp(line, 0, Math.Max(GetLineCount() - 1, 0));
		SetCaretLine(safeLine);
		SetCaretColumn(0);
		CenterViewportToCaret();
		GrabFocus();
	}

	// TODO: Ensure not running on UI thread
	public async Task SetSharpIdeFile(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition = null)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // get off the UI thread
		using var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(SetSharpIdeFile)}");
		var fileContextVersion = AdvanceFileContextVersion();
		_isPreviewMode = false;
		if (!_ownsFileLifecycle || !ReferenceEquals(_currentFile, file))
		{
			_openTabsFileManager.TrackOpenFile(file);
		}
		var readFileTask = _openTabsFileManager.GetFileTextAsync(file);
		PrepareFileContext(file, ownsFileLifecycle: true);
		var syntaxHighlighting = GetDocumentSyntaxHighlightingSafe(_currentFile);
		var razorSyntaxHighlighting = GetRazorDocumentSyntaxHighlightingSafe(_currentFile);
		var diagnostics = GetDocumentDiagnosticsSafe(_currentFile);
		var analyzerDiagnostics = GetDocumentAnalyzerDiagnosticsSafe(_currentFile);
		await readFileTask;
		var setTextTask = this.InvokeAsync(async () =>
		{
			if (!IsCurrentFileContext(file, fileContextVersion)) return;
			_fileChangingSuppressBreakpointToggleEvent = true;
			SetSyntaxHighlightingModel([], []);
			SetDiagnostics([]);
			SetAnalyzerDiagnostics([]);
			SetProjectDiagnostics([]);
			SetText(await readFileTask);
			_fileChangingSuppressBreakpointToggleEvent = false;
			ClearUndoHistory();
			ClearGitDiffDecorations();
			_gitChangeScrollbarOverlay.RefreshLayout();
			if (fileLinePosition is not null) SetFileLinePosition(fileLinePosition.Value);
			if (file.IsMetadataAsSourceFile) Editable = false;
		});
		_ = Task.GodotRun(async () =>
		{
			await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, setTextTask); // Text must be set before setting syntax highlighting
			if (!IsCurrentFileContext(file, fileContextVersion)) return;
			await this.InvokeAsync(async () =>
			{
				if (!IsCurrentFileContext(file, fileContextVersion)) return;
				SetSyntaxHighlightingModel(await syntaxHighlighting, await razorSyntaxHighlighting);
			});
			await diagnostics;
			if (!IsCurrentFileContext(file, fileContextVersion)) return;
			await this.InvokeAsync(async () =>
			{
				if (!IsCurrentFileContext(file, fileContextVersion)) return;
				SetDiagnostics(await diagnostics);
			});
			await analyzerDiagnostics;
			if (!IsCurrentFileContext(file, fileContextVersion)) return;
			await this.InvokeAsync(async () =>
			{
				if (!IsCurrentFileContext(file, fileContextVersion)) return;
				SetAnalyzerDiagnostics(await analyzerDiagnostics);
			});
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
		var traceOperation = _pendingGitDiffTraceOperation;
		using var redrawActivity = traceOperation?.StartChild($"{nameof(SharpIdeCodeEdit)}.GitDiffRedraw");
		redrawActivity?.SetTag("git.diff.redraw_target", _pendingGitDiffTraceTarget.ToString());
		RenderingServer.Singleton.CanvasItemClear(_behindInlineCanvasItemRid!.Value);
		RenderingServer.Singleton.CanvasItemClear(_aboveCanvasItemRid!.Value);
		using (traceOperation?.StartChild($"{nameof(SharpIdeCodeEdit)}.{nameof(DrawGitDiffLineSeamFill)}"))
		{
			DrawGitDiffLineSeamFill();
		}

		using (traceOperation?.StartChild($"{nameof(SharpIdeCodeEdit)}.{nameof(DrawGitDiffInlineHighlights)}"))
		{
			DrawGitDiffInlineHighlights();
		}

		if (!_isPreviewMode)
		{
			foreach (var sharpIdeDiagnostic in _fileDiagnostics.Concat(_fileAnalyzerDiagnostics).ConcatFast(_projectDiagnosticsForFile))
			{
				var line = sharpIdeDiagnostic.Span.Start.Line;
				var startCol = sharpIdeDiagnostic.Span.Start.Character;
				var endCol = sharpIdeDiagnostic.Span.End.Character;
				var color = sharpIdeDiagnostic.Diagnostic.Severity switch
				{
					DiagnosticSeverity.Error => new Color(1, 0, 0),
					DiagnosticSeverity.Warning => new Color("ffb700"),
					_ => new Color(0, 1, 0)
				};
				UnderlineRange(line, startCol, endCol, color);
			}
		}
		DrawCompletionsPopup();
		if (traceOperation is not null)
		{
			_pendingGitDiffTraceOperation = null;
			traceOperation.MarkRedrawCompleted(_pendingGitDiffTraceTarget);
			_pendingGitDiffTraceTarget = GitDiffTraceRedrawTarget.None;
		}
	}

	private void DrawGitDiffInlineHighlights()
	{
		foreach (var (rect, fillColor) in EnumerateVisibleGitDiffInlineHighlights())
		{
			RenderingServer.Singleton.CanvasItemAddRect(_behindInlineCanvasItemRid!.Value, rect, fillColor);
		}
	}

	private void DrawGitDiffLineSeamFill()
	{
		foreach (var (line, fillColor) in _gitDiffLineBackgrounds)
		{
			if (!TryBuildVisibleLineSeamFillRect(line, out var rect))
			{
				continue;
			}

			RenderingServer.Singleton.CanvasItemAddRect(_behindInlineCanvasItemRid!.Value, rect, fillColor);
		}
	}

	internal IEnumerable<(Rect2 Rect, Color FillColor)> EnumerateVisibleGitDiffInlineHighlights()
	{
		foreach (var (line, spans) in _gitDiffInlineHighlights)
		{
			if (line < 0 || line >= GetLineCount()) continue;
			var lineLength = GetLine(line).Length;
			foreach (var decoration in spans)
			{
				var span = decoration.Span;
				if (span.Length <= 0) continue;
				if (span.StartColumn < 0 || span.StartColumn >= lineLength) continue;
				var endColumnExclusive = Math.Min(lineLength, span.StartColumn + span.Length);
				if (endColumnExclusive <= span.StartColumn) continue;

				if (!TryBuildVisibleInlineHighlightRect(line, span.StartColumn, endColumnExclusive, out var rect))
				{
					continue;
				}

				var fillColor = GitDiffPalette.GetInlineBackground(span.HighlightKind, decoration.IsStaged);
				fillColor.A = Mathf.Clamp(fillColor.A * AboveTextInlineHighlightOpacityScale, 0.14f, 0.4f);
				yield return (rect, fillColor);
			}
		}
	}

	internal bool TryBuildVisibleInlineHighlightRect(int line, int startColumn, int endColumnExclusive, out Rect2 rect)
	{
		rect = default;
		Rect2? firstVisibleRect = null;
		Rect2? lastVisibleRect = null;
		for (var column = startColumn; column < endColumnExclusive; column++)
		{
			var charRect = GetRectAtCharacterIndex(line, column);
			if (charRect.Position.X < 0f || charRect.Position.Y < 0f)
			{
				continue;
			}

			firstVisibleRect ??= charRect;
			lastVisibleRect = charRect;
		}

		if (!firstVisibleRect.HasValue || !lastVisibleRect.HasValue)
		{
			return false;
		}

		var left = firstVisibleRect.Value.Position.X;
		var right = lastVisibleRect.Value.End.X;
		rect = new Rect2(
			new Vector2(left, firstVisibleRect.Value.Position.Y),
			new Vector2(Mathf.Max(1f, right - left), firstVisibleRect.Value.Size.Y));
		return true;
	}

	private bool TryBuildVisibleLineSeamFillRect(int line, out Rect2 rect)
	{
		rect = default;
		if (line < 0 || line >= GetLineCount())
		{
			return false;
		}

		var lineRect = GetRectAtLineColumn(line, 0);
		var fillWidth = Mathf.Clamp(lineRect.Position.X, 0f, Size.X);
		if (fillWidth <= 0f)
		{
			return false;
		}

		var top = Mathf.Clamp(lineRect.Position.Y, 0f, Size.Y);
		var bottom = Mathf.Clamp(lineRect.End.Y, 0f, Size.Y);
		if (bottom <= top)
		{
			return false;
		}

		rect = new Rect2(new Vector2(0f, top), new Vector2(fillWidth, bottom - top));
		return true;
	}

	internal Rect2 GetRectAtCharacterIndex(int line, int characterIndex)
	{
		return GetRectAtLineColumn(line, characterIndex <= 0 ? 0 : characterIndex + 1);
	}

	// This only gets invoked if the Node is focused
	public override void _GuiInput(InputEvent @event)
	{
		if (_isPreviewMode)
		{
			base._GuiInput(@event);
			return;
		}
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
			if (current is null || current.File != _currentFile || current.LinePosition.Line != line) // Only record a new navigation if the line has changed, or this editor is becoming the active navigation target.
			{
				_navigationHistoryService.RecordNavigation(_currentFile, new SharpIdeFileLinePosition(line, col));
			}
		}
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		CloseSymbolHoverWindow();
		if (_isPreviewMode) return;
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
		var breakpointed = SupportsLineStatusQueries() && IsLineBreakpointed(line);
		var executing = SupportsLineStatusQueries() && IsLineExecuting(line);
		var lineColour = (breakpointed, executing) switch
		{
			(_, true) => _executingLineColor,
			(true, false) => _breakpointLineColor,
			(false, false) => _gitDiffLineBackgrounds.GetValueOrDefault(line, Colors.Transparent)
		};
		SetLineBackgroundColor(line, lineColour);
	}

	public void SetGitDiffLineBackgrounds(IReadOnlyDictionary<int, Color> lineColors)
	{
		var affectedLines = _gitDiffLineBackgrounds.Keys.Concat(lineColors.Keys).Distinct().ToList();
		_gitDiffLineBackgrounds.Clear();
		foreach (var (line, color) in lineColors)
		{
			if (line < 0 || line >= GetLineCount()) continue;
			_gitDiffLineBackgrounds[line] = color;
		}

		foreach (var line in affectedLines)
		{
			if (line < 0 || line >= GetLineCount()) continue;
			SetLineColour(line);
		}

		QueueRedraw();
	}

	private bool SupportsLineStatusQueries()
	{
		return GetGutterCount() > 0;
	}

	public void ClearGitDiffLineBackgrounds()
	{
		if (_gitDiffLineBackgrounds.Count is 0) return;
		var affectedLines = _gitDiffLineBackgrounds.Keys.ToList();
		_gitDiffLineBackgrounds.Clear();
		foreach (var line in affectedLines)
		{
			if (line < 0 || line >= GetLineCount()) continue;
			SetLineColour(line);
		}

		QueueRedraw();
	}

	public void SetGitDiffInlineHighlights(IReadOnlyDictionary<int, IReadOnlyList<GitDiffInlineDecoration>> highlights)
	{
		_gitDiffInlineHighlights.Clear();
		foreach (var (line, spans) in highlights)
		{
			if (line < 0 || line >= GetLineCount()) continue;
			_gitDiffInlineHighlights[line] = spans;
		}

		QueueRedraw();
	}

	public void ClearGitDiffInlineHighlights()
	{
		if (_gitDiffInlineHighlights.Count is 0) return;
		_gitDiffInlineHighlights.Clear();
		QueueRedraw();
	}

	public void SetGitDiffScrollMarkers(IReadOnlyList<GitDiffScrollMarker> markers)
	{
		_gitChangeScrollbarOverlay.SetMarkers(markers);
	}

	internal void SetPendingGitDiffTraceOperation(GitDiffTraceOperation? traceOperation, GitDiffTraceRedrawTarget redrawTarget)
	{
		_pendingGitDiffTraceOperation = traceOperation;
		_pendingGitDiffTraceTarget = redrawTarget;
	}

	public void ClearGitDiffScrollMarkers()
	{
		_gitChangeScrollbarOverlay.ClearMarkers();
	}

	private void ClearGitDiffDecorations()
	{
		ClearGitDiffLineBackgrounds();
		ClearGitDiffInlineHighlights();
		ClearGitDiffScrollMarkers();
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

	public async Task SetPreviewTextForFile(SharpIdeFile file, string text, bool editable, bool clearGitDiffDecorations = true)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		using var __ = SharpIdeOtel.Source.StartActivity($"{nameof(SharpIdeCodeEdit)}.{nameof(SetPreviewTextForFile)}");
		var fileContextVersion = AdvanceFileContextVersion();
		_isPreviewMode = true;
		if (clearGitDiffDecorations)
		{
			await this.InvokeAsync(ClearGitDiffDecorations);
		}
		PrepareFileContext(file, ownsFileLifecycle: false, subscribeToProjectDiagnostics: false);
		var syntaxHighlighting = GetDocumentSyntaxHighlightingSafe(_currentFile, text);
		var razorSyntaxHighlighting = GetRazorDocumentSyntaxHighlightingSafe(_currentFile);
		var setTextTask = this.InvokeAsync(() =>
		{
			if (!IsCurrentFileContext(file, fileContextVersion)) return;
			_fileChangingSuppressBreakpointToggleEvent = true;
			_settingWholeDocumentTextSuppressLineEditsEvent = true;
			SetSyntaxHighlightingModel([], []);
			SetDiagnostics([]);
			SetAnalyzerDiagnostics([]);
			SetProjectDiagnostics([]);
			SetText(text);
			_settingWholeDocumentTextSuppressLineEditsEvent = false;
			_fileChangingSuppressBreakpointToggleEvent = false;
			ClearUndoHistory();
			Editable = editable;
			_gitChangeScrollbarOverlay.RefreshLayout();
		});
		_ = Task.GodotRun(async () =>
		{
			await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, setTextTask);
			if (!IsCurrentFileContext(file, fileContextVersion)) return;
			await this.InvokeAsync(async () =>
			{
				if (!IsCurrentFileContext(file, fileContextVersion)) return;
				SetSyntaxHighlightingModel(await syntaxHighlighting, await razorSyntaxHighlighting);
			});
		});
	}

	private void PrepareFileContext(SharpIdeFile file, bool ownsFileLifecycle, bool subscribeToProjectDiagnostics = true)
	{
		_projectDiagnosticsObserveDisposable?.Dispose();
		if (_ownsFileLifecycle && _currentFile is not null)
		{
			if (!ReferenceEquals(_currentFile, file))
			{
				_openTabsFileManager.CloseFile(_currentFile);
			}
			_currentFile.FileContentsChangedExternally.Unsubscribe(OnFileChangedExternally);
			_currentFile.FileDeleted.Unsubscribe(OnFileDeleted);
		}

		_currentFile = file;
		_ownsFileLifecycle = ownsFileLifecycle;
		if (!subscribeToProjectDiagnostics)
		{
			_projectDiagnosticsForFile = [];
		}
		if (ownsFileLifecycle)
		{
			_currentFile.FileContentsChangedExternally.Subscribe(OnFileChangedExternally);
			_currentFile.FileDeleted.Subscribe(OnFileDeleted);
		}

		if (!subscribeToProjectDiagnostics)
		{
			return;
		}

		var project = ((IChildSharpIdeNode)_currentFile).GetNearestProjectNode();
		if (project is null) return;

		_projectDiagnosticsObserveDisposable = project.Diagnostics.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (innerEvent, ct) =>
			{
				var projectDiagnosticsForFile = project.Diagnostics.Where(s => s.FilePath == _currentFile.Path).ToImmutableArray();
				await this.InvokeAsync(() => SetProjectDiagnostics(projectDiagnosticsForFile));
			});
	}

	private long AdvanceFileContextVersion() => Interlocked.Increment(ref _fileContextVersion);

	private long ReadFileContextVersion() => Interlocked.Read(ref _fileContextVersion);

	private bool IsCurrentFileContext(SharpIdeFile file, long fileContextVersion)
	{
		return fileContextVersion == ReadFileContextVersion() && ReferenceEquals(_currentFile, file);
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
