using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Threading;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Analysis.Razor;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.CodeEditor;
using SharpIDE.Godot.Features.IdeSettings;
using Task = System.Threading.Tasks.Task;

namespace SharpIDE.Godot.Features.Debug_.Tab.SubTabs;

// 🤖
public partial class DebuggerEvalExpressionCodeEdit : CodeEdit
{
	private const int MaxHistoryCount = 100;

	public event Action<string>? ExpressionSubmitted;

	private readonly CustomHighlighter _syntaxHighlighter = new();
	private readonly Lock _requestLock = new();
	private readonly List<string> _history = [];
	private CanvasItem _aboveCanvasItem = null!;
	private Rid _aboveCanvasItemRid;
	private DebuggerExpressionIntelliSenseSession? _session;
	private DebuggerExpressionCompletionResult? _completionResult;
	private CompletionTrigger? _completionTrigger;
	private ImmutableArray<SharpIdeDiagnostic> _diagnostics = [];
	private CancellationTokenSource? _analysisCancellationTokenSource;
	private CancellationTokenSource? _completionCancellationTokenSource;
	private CancellationTokenSource? _symbolCancellationTokenSource;
	private CodeCompletionPopup _completionPopup = null!;
	private SymbolHoverPopup _symbolHoverPopup = null!;
	private string _storedEvalTextWhileNavigatingHistory = string.Empty;
	private int _historyIndex;
	private readonly CancellationSeries _contextCancellationSeries = new();

	[Inject] private readonly RoslynAnalysis _roslynAnalysis = null!;

	public override void _Ready()
	{
		_aboveCanvasItem = GetNode<CanvasItem>("%AboveCanvasItem");
		_aboveCanvasItemRid = _aboveCanvasItem.GetCanvasItem();
		RenderingServer.Singleton.CanvasItemSetParent(_aboveCanvasItemRid, GetCanvasItem());
		UpdateEditorTheme(Singletons.AppState.IdeSettings.Theme);
		SyntaxHighlighter = _syntaxHighlighter;
		CodeCompletionEnabled = false;
		Editable = false;
		var completionDescriptionWindow = GetNode<Window>("%CompletionDescriptionWindow");
		var completionDescriptionLabel = completionDescriptionWindow.GetNode<RichTextLabel>("PanelContainer/RichTextLabel");
		_completionPopup = new CodeCompletionPopup(this, _aboveCanvasItemRid, completionDescriptionWindow, completionDescriptionLabel, _syntaxHighlighter, GetCompletionDescriptionAsync,
			trigger => QueueCompletionRequest(trigger, checkTrigger: false), ApplyCompletion, preferAbove: true);
		_symbolHoverPopup = new SymbolHoverPopup(this, preferAbove: true);
		TextChanged += OnTextChanged;
		SymbolHovered += (symbol, line, column) => _ = Task.GodotRun(() => OnSymbolHovered(symbol, line, column));
		SymbolValidate += _ => SetSymbolLookupWordAsValid(true);
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Subscribe(UpdateEditorThemeAsync);
		GetHScrollBar().ValueChanged += _ => QueueRedraw();
		GetVScrollBar().ValueChanged += _ => QueueRedraw();
	}

	public async Task SetContextAsync(SharpIdeFile file, LinePosition sourceContextPosition, CancellationToken cancellationToken = default)
	{
		var contextCancellationToken = _contextCancellationSeries.CreateNext(cancellationToken);
		CancelPendingRequests();
		var newSession = await _roslynAnalysis.CreateDebuggerExpressionIntelliSenseSessionAsync(file, sourceContextPosition, cancellationToken);
		if (contextCancellationToken.IsCancellationRequested)
		{
			newSession?.Dispose();
			return;
		}

		await this.InvokeAsync(() =>
		{
			_session?.Dispose();
			_session = newSession;
			Editable = newSession is not null;
			_completionPopup.Reset();
			_completionResult = null;
			_completionTrigger = null;
			QueueAnalysis();
		});
	}

	public void ClearContext()
	{
		_contextCancellationSeries.CreateNext();
		CancelPendingRequests();
		_session?.Dispose();
		_session = null;
		_completionResult = null;
		_completionTrigger = null;
		_diagnostics = [];
		Editable = false;
		_completionPopup.Reset();
		SetSyntaxHighlighting([]);
		_historyIndex = _history.Count;
		_storedEvalTextWhileNavigatingHistory = string.Empty;
		ReplaceText(string.Empty);
		QueueRedraw();
	}

	public override void _ExitTree()
	{
		GodotGlobalEvents.Instance.TextEditorThemeChanged.Unsubscribe(UpdateEditorThemeAsync);
		ClearContext();
		_symbolHoverPopup.Close();
		_completionPopup.Dispose();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationThemeChanged)
		{
			CallDeferred(CanvasItem.MethodName.QueueRedraw);
		}
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false })
		{
			_symbolHoverPopup.Close();
		}

		if (_completionPopup.TryConsumeGuiInput(@event))
		{
			AcceptEvent();
			return;
		}

		if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } keyEvent)
		{
			if (keyEvent.Echo is false && string.IsNullOrWhiteSpace(Text) is false)
			{
				var expression = Text;
				_history.Add(expression);
				if (_history.Count > MaxHistoryCount)
				{
					_history.RemoveAt(0);
				}

				_historyIndex = _history.Count;
				_storedEvalTextWhileNavigatingHistory = string.Empty;
				ExpressionSubmitted?.Invoke(expression);
				ReplaceText(string.Empty);
			}

			AcceptEvent();
			return;
		}

		if (@event is InputEventKey { Pressed: true, Keycode: Key.Up or Key.Down } historyKeyEvent && TryNavigateHistory(historyKeyEvent.Keycode is Key.Up ? -1 : 1))
		{
			AcceptEvent();
			return;
		}

		if (@event.IsActionPressed(InputStringNames.Copy) && _symbolHoverPopup.TryCopySelectedText())
		{
			AcceptEvent();
		}
	}

	private bool TryNavigateHistory(int direction)
	{
		if (_history.Count is 0) return false;
		if (direction < 0)
		{
			if (_historyIndex == _history.Count) _storedEvalTextWhileNavigatingHistory = Text;
			_historyIndex = Math.Max(0, _historyIndex - 1);
		}
		else
		{
			if (_historyIndex == _history.Count) return false;
			_historyIndex++;
		}

		var historyText = _historyIndex == _history.Count ? _storedEvalTextWhileNavigatingHistory : _history[_historyIndex];
		ReplaceText(historyText);
		SetCaretLine(0);
		SetCaretColumn(historyText.Length);
		return true;
	}

	private void ApplyCompletion(CompletionItem completionItem)
	{
		var session = _session;
		var completionResult = _completionResult;
		if (session is null || completionResult is null)
		{
			return;
		}

		var expressionText = Text;
		var caretLine = GetCaretLine();
		var caretColumn = GetCaretColumn();
		_completionResult = null;
		_completionTrigger = null;
		_ = Task.GodotRun(async () =>
		{
			try
			{
				var change = await session.GetCompletionChangeAsync(completionResult, completionItem, expressionText, new LinePosition(caretLine, caretColumn));
				await this.InvokeAsync(() =>
				{
					if (ReferenceEquals(session, _session) is false || expressionText != Text)
					{
						return;
					}

					ReplaceText(change.Text);
					SetCaretLine(change.CaretPosition.Line);
					SetCaretColumn(change.CaretPosition.Character);
				});
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
			{
			}
		});
	}

	public override void _Draw()
	{
		RenderingServer.Singleton.CanvasItemClear(_aboveCanvasItemRid);
		foreach (var diagnostic in _diagnostics)
		{
			var color = diagnostic.Diagnostic.Severity switch
			{
				DiagnosticSeverity.Error => new Color(1, 0, 0),
				DiagnosticSeverity.Warning => new Color("ffb700"), _ => new Color(0, 1, 0)
			};
			for (var line = diagnostic.Span.Start.Line; line <= diagnostic.Span.End.Line; line++)
			{
				var startColumn = line == diagnostic.Span.Start.Line ? diagnostic.Span.Start.Character : 0;
				var endColumn = line == diagnostic.Span.End.Line ? diagnostic.Span.End.Character : GetLine(line).Length;
				UnderlineRange(line, startColumn, endColumn, color);
			}
		}
		_completionPopup.Draw();
	}

	private void OnTextChanged()
	{
		if (GetLineCount() > 1)
		{
			var caretColumn = GetCaretColumn();
			for (var line = 0; line < GetCaretLine(); line++)
			{
				caretColumn += GetLine(line).Length;
			}

			ReplaceText(Text.Replace("\n", ""));
			SetCaretLine(0);
			SetCaretColumn(caretColumn);
			return;
		}

		var completionTrigger = _completionPopup.TakePendingCompletionTrigger();
		var filterReason = _completionPopup.TakePendingFilterReason();
		QueueAnalysis();
		if (completionTrigger is not null)
		{
			QueueCompletionRequest(completionTrigger.Value, checkTrigger: true);
		}
		else if (filterReason is not null)
		{
			QueueCompletionFilter(filterReason.Value);
		}
	}

	// Syntax highlighting is a pain - using SetText doesn't update the highlighting...
	private void ReplaceText(string text)
	{
		BeginComplexOperation();
		var lastLine = GetLineCount() - 1;
		var lastColumn = GetLine(lastLine).Length;
		if (lastLine > 0 || lastColumn > 0)
		{
			RemoveText(0, 0, lastLine, lastColumn);
		}

		SetCaretLine(0);
		SetCaretColumn(0);
		if (text.Length > 0)
		{
			InsertTextAtCaret(text);
		}
		EndComplexOperation();
	}

	private void QueueAnalysis()
	{
		var session = _session;
		if (session is null)
		{
			return;
		}

		var expressionText = Text;
		var cancellationToken = ReplaceCancellationTokenSource(ref _analysisCancellationTokenSource);
		_ = Task.GodotRun(async () =>
		{
			try
			{
				var result = await session.AnalyzeAsync(expressionText, cancellationToken);
				await this.InvokeAsync(() =>
				{
					if (ReferenceEquals(session, _session) is false || expressionText != Text)
					{
						return;
					}

					_diagnostics = result.Diagnostics;
					SetSyntaxHighlighting(result.ClassifiedSpans);
					QueueRedraw();
				});
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
			{
			}
		});
	}

	private void QueueCompletionRequest(CompletionTrigger trigger, bool checkTrigger)
	{
		var session = _session;
		if (session is null)
		{
			return;
		}

		var expressionText = Text;
		var caretLine = GetCaretLine();
		var caretColumn = GetCaretColumn();
		var caretPosition = new LinePosition(caretLine, caretColumn);
		var cancellationToken = ReplaceCancellationTokenSource(ref _completionCancellationTokenSource);
		_ = Task.GodotRun(async () =>
		{
			try
			{
				if (checkTrigger && await session.ShouldTriggerCompletionAsync(expressionText, caretPosition, trigger, cancellationToken) is false)
				{
					return;
				}

				var result = await session.GetCompletionsAsync(expressionText, caretPosition, trigger, cancellationToken);
				var filterReason = GetFilterReason(trigger);
				var options = session.FilterCompletions(result, expressionText, caretPosition, trigger, filterReason);
				await this.InvokeAsync(() =>
				{
					if (cancellationToken.IsCancellationRequested is false)
					{
						ShowCompletions(session, expressionText, trigger, result, options);
					}
				});
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
			{
			}
		});
	}

	private void QueueCompletionFilter(CompletionFilterReason filterReason)
	{
		var session = _session;
		var completionResult = _completionResult;
		var completionTrigger = _completionTrigger;
		if (session is null || completionResult is null || completionTrigger is null)
		{
			return;
		}

		var expressionText = Text;
		var caretPosition = new LinePosition(GetCaretLine(), GetCaretColumn());
		var cancellationToken = ReplaceCancellationTokenSource(ref _completionCancellationTokenSource);
		_ = Task.GodotRun(async () =>
		{
			try
			{
				var options = session.FilterCompletions(completionResult, expressionText, caretPosition, completionTrigger.Value, filterReason);
				await this.InvokeAsync(() =>
				{
					if (cancellationToken.IsCancellationRequested || ReferenceEquals(session, _session) is false || expressionText != Text)
					{
						return;
					}

					_completionPopup.SetOptions(options);
				});
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
			{
			}
		});
	}

	private void ShowCompletions(DebuggerExpressionIntelliSenseSession session, string expressionText, CompletionTrigger trigger, DebuggerExpressionCompletionResult result, ImmutableArray<SharpIdeCompletionItem> options)
	{
		if (ReferenceEquals(session, _session) is false || expressionText != Text)
		{
			return;
		}

		_completionResult = result;
		_completionTrigger = trigger;
		var expressionSourceText = SourceText.From(expressionText);
		var localTriggerPosition = Math.Clamp(result.CompletionList.Span.Start - result.ExpressionStart, 0, expressionSourceText.Length);
		var triggerPosition = expressionSourceText.Lines.GetLinePosition(localTriggerPosition);
		_completionPopup.SetTriggerPosition(GetPosAtLineColumn(triggerPosition.Line, triggerPosition.Character));
		_completionPopup.SetOptions(options);
	}

	private async Task OnSymbolHovered(string _, long line, long column)
	{
		var session = _session;
		if (session is null)
		{
			return;
		}

		var expressionText = Text;
		var linePosition = new LinePosition((int) line, (int) column);
		var cancellationToken = ReplaceCancellationTokenSource(ref _symbolCancellationTokenSource);
		try
		{
			var (symbol, symbolSpan) = await session.LookupSymbolAsync(expressionText, linePosition, cancellationToken);
			var diagnostic = _diagnostics.FirstOrDefault(candidate => Contains(candidate.Span, linePosition));
			if (symbol is null && diagnostic is null)
			{
				return;
			}

			await this.InvokeAsync(() =>
			{
				if (ReferenceEquals(session, _session) && expressionText == Text)
				{
					_symbolHoverPopup.Show(symbol, symbolSpan, diagnostic, linePosition);
				}
			});
		}
		catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
		{
		}
	}

	private void SetSyntaxHighlighting(ImmutableArray<SharpIdeClassifiedSpan> classifiedSpans)
	{
		_syntaxHighlighter.SetHighlightingData(classifiedSpans, ImmutableArray<SharpIdeRazorClassifiedSpan>.Empty);
		_syntaxHighlighter.UpdateCache();
		SyntaxHighlighter = null;
		SyntaxHighlighter = _syntaxHighlighter;
	}

	private void UnderlineRange(int line, int startColumn, int endColumn, Color color)
	{
		if (line < 0 || line >= GetLineCount() || startColumn > endColumn)
		{
			return;
		}

		var lineLength = GetLine(line).Length;
		startColumn = Math.Clamp(startColumn, 0, lineLength);
		endColumn = Math.Clamp(endColumn, 0, lineLength);
		var startRect = GetRectAtLineColumn(line, startColumn);
		var endRect = GetRectAtLineColumn(line, endColumn);
		var startPosition = startRect.End;
		if (startColumn is 0)
		{
			startPosition.X -= startRect.Size.X;
		}

		var endPosition = endRect.End;
		startPosition.Y -= 3;
		endPosition.Y -= 3;
		if (startColumn == endColumn)
		{
			endPosition.X += 10;
		}

		RenderingServer.Singleton.DrawDashedLine(_aboveCanvasItemRid, startPosition, endPosition, color, 1.5f);
	}

	private void CancelPendingRequests()
	{
		lock (_requestLock)
		{
			CancelAndDispose(ref _analysisCancellationTokenSource);
			CancelAndDispose(ref _completionCancellationTokenSource);
			CancelAndDispose(ref _symbolCancellationTokenSource);
		}
	}

	private CancellationToken ReplaceCancellationTokenSource(ref CancellationTokenSource? cancellationTokenSource)
	{
		lock (_requestLock)
		{
			CancelAndDispose(ref cancellationTokenSource);
			cancellationTokenSource = new CancellationTokenSource();
			return cancellationTokenSource.Token;
		}
	}

	private static void CancelAndDispose(ref CancellationTokenSource? cancellationTokenSource)
	{
		cancellationTokenSource?.Cancel();
		cancellationTokenSource?.Dispose();
		cancellationTokenSource = null;
	}

	private Task UpdateEditorThemeAsync(LightOrDarkTheme theme)
	{
		UpdateEditorTheme(theme);
		return Task.CompletedTask;
	}

	private void UpdateEditorTheme(LightOrDarkTheme theme) => _syntaxHighlighter.UpdateThemeColorCache(theme);

	private Task<CompletionDescription> GetCompletionDescriptionAsync(CompletionItem completionItem, CancellationToken cancellationToken)
	{
		var session = _session ?? throw new ObjectDisposedException(nameof(DebuggerExpressionIntelliSenseSession));
		var completionResult = _completionResult ?? throw new InvalidOperationException("The debugger completion result is unavailable.");
		return session.GetCompletionDescriptionAsync(completionResult, completionItem, cancellationToken);
	}

	private static CompletionFilterReason GetFilterReason(CompletionTrigger trigger) => trigger.Kind switch
	{
		CompletionTriggerKind.Insertion => CompletionFilterReason.Insertion, CompletionTriggerKind.Deletion => CompletionFilterReason.Deletion, CompletionTriggerKind.InvokeAndCommitIfUnique => CompletionFilterReason.Other,
		_ => throw new ArgumentOutOfRangeException(nameof(trigger.Kind), trigger.Kind, null),
	};

	private static bool Contains(LinePositionSpan span, LinePosition position) => position >= span.Start && position <= span.End;
}
