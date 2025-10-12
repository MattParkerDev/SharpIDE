using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.RazorAccess;
using Task = System.Threading.Tasks.Task;
using Timer = Godot.Timer;

namespace SharpIDE.Godot.Features.CodeEditor;

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

	private ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)> _diagnostics = [];
	private ImmutableArray<CodeAction> _currentCodeActionsInPopup = [];
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
	}

	public override void _ExitTree()
	{
		_currentFile?.FileContentsChangedExternallyFromDisk.Unsubscribe(OnFileChangedExternallyFromDisk);
		_currentFile?.FileContentsChangedExternally.Unsubscribe(OnFileChangedExternallyInMemory);
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
		//var valid = symbol.Contains(' ') is false;
		//SetSymbolLookupWordAsValid(valid);
		SetSymbolLookupWordAsValid(true);
	}

	// This method is a bit of a disaster - we create an additional invisible Window, so that the tooltip window doesn't disappear while the mouse is over the hovered symbol
	private async void OnSymbolHovered(string symbol, long line, long column)
	{
		if (HasFocus() is false) return; // only show if we have focus, every tab is currently listening for this event, maybe find a better way
		var globalMousePosition = GetGlobalMousePosition(); // don't breakpoint before this, else your mouse position will be wrong
		var lineHeight = GetLineHeight();
		GD.Print($"Symbol hovered: {symbol} at line {line}, column {column}");
		
		var (roslynSymbol, linePositionSpan) = await RoslynAnalysis.LookupSymbol(_currentFile, new LinePosition((int)line, (int)column));
		if (roslynSymbol is null || linePositionSpan is null)
		{
			return;
		}

		var symbolNameHoverWindow = new Window();
		symbolNameHoverWindow.WrapControls = true;
		symbolNameHoverWindow.Unresizable = true;
		symbolNameHoverWindow.Transparent = true;
		symbolNameHoverWindow.Borderless = true;
		symbolNameHoverWindow.PopupWMHint = true;
		symbolNameHoverWindow.PopupWindow = true;
		symbolNameHoverWindow.MinimizeDisabled = true;
		symbolNameHoverWindow.MaximizeDisabled = true;
		
		var startSymbolCharRect = GetRectAtLineColumn(linePositionSpan.Value.Start.Line, linePositionSpan.Value.Start.Character + 1);
		var endSymbolCharRect = GetRectAtLineColumn(linePositionSpan.Value.End.Line, linePositionSpan.Value.End.Character + 1);
		symbolNameHoverWindow.Size = new Vector2I(endSymbolCharRect.End.X - startSymbolCharRect.Position.X, lineHeight);
		
		var globalPosition = GetGlobalPosition();
		var startSymbolCharGlobalPos = startSymbolCharRect.Position + globalPosition;
		var endSymbolCharGlobalPos = endSymbolCharRect.Position + globalPosition;
		
		AddChild(symbolNameHoverWindow);
		symbolNameHoverWindow.Position = new Vector2I((int)startSymbolCharGlobalPos.X, (int)endSymbolCharGlobalPos.Y);
		symbolNameHoverWindow.Popup();
		
		var tooltipWindow = new Window();
		tooltipWindow.WrapControls = true;
		tooltipWindow.Unresizable = true;
		tooltipWindow.Transparent = true;
		tooltipWindow.Borderless = true;
		tooltipWindow.PopupWMHint = true;
		tooltipWindow.PopupWindow = true;
		tooltipWindow.MinimizeDisabled = true;
		tooltipWindow.MaximizeDisabled = true;
		
		var timer = new Timer { WaitTime = 0.05f, OneShot = true, Autostart = false };
		tooltipWindow.AddChild(timer);
		timer.Timeout += () =>
		{
			tooltipWindow.QueueFree();
			symbolNameHoverWindow.QueueFree();
		};
	
		tooltipWindow.MouseExited += () => timer.Start();
		tooltipWindow.MouseEntered += () => timer.Stop();
		symbolNameHoverWindow.MouseExited += () => timer.Start();
		symbolNameHoverWindow.MouseEntered += () => timer.Stop();
		
		var styleBox = new StyleBoxFlat
		{
			BgColor = new Color("2b2d30"),
			BorderColor = new Color("3e4045"),
			BorderWidthTop = 1,
			BorderWidthBottom = 1,
			BorderWidthLeft = 1,
			BorderWidthRight = 1,
			CornerRadiusBottomLeft = 4,
			CornerRadiusBottomRight = 4,
			CornerRadiusTopLeft = 4,
			CornerRadiusTopRight = 4,
			ShadowSize = 2,
			ShadowColor = new Color(0, 0, 0, 0.5f),
			ExpandMarginTop = -2, // negative margin seems to fix shadow being cut off?
			ExpandMarginBottom = -2,
			ExpandMarginLeft = -2,
			ExpandMarginRight = -2,
			ContentMarginTop = 10,
			ContentMarginBottom = 10,
			ContentMarginLeft = 12,
			ContentMarginRight = 12
		};
		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", styleBox);
		
		var symbolInfoNode = roslynSymbol switch
		{
			IMethodSymbol methodSymbol => SymbolInfoComponents.GetMethodSymbolInfo(methodSymbol),
			INamedTypeSymbol namedTypeSymbol => SymbolInfoComponents.GetNamedTypeSymbolInfo(namedTypeSymbol),
			IPropertySymbol propertySymbol => SymbolInfoComponents.GetPropertySymbolInfo(propertySymbol),
			IFieldSymbol fieldSymbol => SymbolInfoComponents.GetFieldSymbolInfo(fieldSymbol),
			IParameterSymbol parameterSymbol => SymbolInfoComponents.GetParameterSymbolInfo(parameterSymbol),
			ILocalSymbol localSymbol => SymbolInfoComponents.GetLocalVariableSymbolInfo(localSymbol),
			_ => SymbolInfoComponents.GetUnknownTooltip(roslynSymbol)
		};
		symbolInfoNode.FitContent = true;
		symbolInfoNode.AutowrapMode = TextServer.AutowrapMode.Off;
		symbolInfoNode.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		
		panel.AddChild(symbolInfoNode);
		var vboxContainer = new VBoxContainer();
		vboxContainer.AddThemeConstantOverride("separation", 0);
		vboxContainer.AddChild(panel);
		tooltipWindow.AddChild(vboxContainer);
		tooltipWindow.ChildControlsChanged();
		AddChild(tooltipWindow);
		
		tooltipWindow.Position = new Vector2I((int)globalMousePosition.X, (int)startSymbolCharGlobalPos.Y + lineHeight);
		tooltipWindow.Popup();
	}

	private void OnCaretChanged()
	{
		_selectionStartCol = GetSelectionFromColumn();
		_selectionEndCol = GetSelectionToColumn();
		_currentLine = GetCaretLine();
		// GD.Print($"Selection changed to line {_currentLine}, start {_selectionStartCol}, end {_selectionEndCol}");
	}

	private async void OnTextChanged()
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		_currentFile.IsDirty.Value = true;
		Singletons.FileManager.UpdateFileTextInMemory(_currentFile, Text);
		_ = Task.GodotRun(async () =>
		{
			var syntaxHighlighting = RoslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
			var razorSyntaxHighlighting = RoslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile);
			var diagnostics = RoslynAnalysis.GetDocumentDiagnostics(_currentFile);
			var slnDiagnostics = RoslynAnalysis.UpdateSolutionDiagnostics();
			await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, diagnostics);
			Callable.From(() =>
			{
				SetSyntaxHighlightingModel(syntaxHighlighting.Result, razorSyntaxHighlighting.Result);
				SetDiagnosticsModel(diagnostics.Result);
			}).CallDeferred();
			await slnDiagnostics;
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
			var affectedFiles = await RoslynAnalysis.ApplyCodeActionAsync(codeAction);
			// TODO: This can be more efficient - we can just update in memory and proceed with highlighting etc. Save to disk in background.
			foreach (var (affectedFile, updatedText) in affectedFiles)
			{
				await Singletons.FileManager.UpdateInMemoryIfOpenAndSaveAsync(affectedFile, updatedText);
				affectedFile.FileContentsChangedExternally.InvokeParallelFireAndForget();
			}
		});
	}

	private async Task OnFileChangedExternallyInMemory()
	{
		var fileContents = await Singletons.FileManager.GetFileTextAsync(_currentFile);
		var syntaxHighlighting = RoslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
		var razorSyntaxHighlighting = RoslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile);
		var diagnostics = RoslynAnalysis.GetDocumentDiagnostics(_currentFile);
		var slnDiagnostics = RoslynAnalysis.UpdateSolutionDiagnostics();
		await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, diagnostics);
		Callable.From(() =>
		{
			var currentCaretPosition = GetCaretPosition();
			var vScroll = GetVScroll();
			BeginComplexOperation();
			SetText(fileContents);
			SetSyntaxHighlightingModel(syntaxHighlighting.Result, razorSyntaxHighlighting.Result);
			SetDiagnosticsModel(diagnostics.Result);
			SetCaretLine(currentCaretPosition.line);
			SetCaretColumn(currentCaretPosition.col);
			SetVScroll(vScroll);
			EndComplexOperation();
		}).CallDeferred();
		await slnDiagnostics;
	}

	public void SetFileLinePosition(SharpIdeFileLinePosition fileLinePosition)
	{
		var line = fileLinePosition.Line - 1;
		var column = fileLinePosition.Column - 1;
		SetCaretLine(line);
		SetCaretColumn(column);
		CenterViewportToCaret();
		GrabFocus();
	}

	// TODO: Ensure not running on UI thread
	public async Task SetSharpIdeFile(SharpIdeFile file)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // get off the UI thread
		_currentFile = file;
		var readFileTask = Singletons.FileManager.GetFileTextAsync(file);
		_currentFile.FileContentsChangedExternally.Subscribe(OnFileChangedExternallyInMemory);
		_currentFile.FileContentsChangedExternallyFromDisk.Subscribe(OnFileChangedExternallyFromDisk);
		
		var syntaxHighlighting = RoslynAnalysis.GetDocumentSyntaxHighlighting(_currentFile);
		var razorSyntaxHighlighting = RoslynAnalysis.GetRazorDocumentSyntaxHighlighting(_currentFile);
		var diagnostics = RoslynAnalysis.GetDocumentDiagnostics(_currentFile);
		var setTextTask = this.InvokeAsync(async () =>
		{
			_fileChangingSuppressBreakpointToggleEvent = true;
			SetText(await readFileTask);
			_fileChangingSuppressBreakpointToggleEvent = false;
		});
		await Task.WhenAll(syntaxHighlighting, razorSyntaxHighlighting, setTextTask); // Text must be set before setting syntax highlighting
		SetSyntaxHighlightingModel(await syntaxHighlighting, await razorSyntaxHighlighting);
		SetDiagnosticsModel(await diagnostics);
	}

	private async Task OnFileChangedExternallyFromDisk()
	{
		await Singletons.FileManager.ReloadFileFromDisk(_currentFile);
		await OnFileChangedExternallyInMemory();
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
		if (HasFocus() is false) return; // every tab is currently listening for this input. Only respond if we have focus. Consider refactoring this _UnhandledKeyInput to CodeEditorPanel
		if (@event.IsActionPressed(InputStringNames.CodeFixes))
		{
			EmitSignalCodeFixesRequested();
		}
		else if (@event.IsActionPressed(InputStringNames.SaveAllFiles))
		{
			_ = Task.GodotRun(async () =>
			{
				await Singletons.FileManager.SaveAllOpenFilesAsync();
			});
		}
		else if (@event.IsActionPressed(InputStringNames.SaveFile))
		{
			_ = Task.GodotRun(async () =>
			{
				await Singletons.FileManager.SaveFileAsync(_currentFile);
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

	private void SetDiagnosticsModel(ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)> diagnostics)
	{
		_diagnostics = diagnostics;
	}

	private void SetSyntaxHighlightingModel(IEnumerable<(FileLinePositionSpan fileSpan, ClassifiedSpan classifiedSpan)> classifiedSpans, IEnumerable<SharpIdeRazorClassifiedSpan> razorClassifiedSpans)
	{
		_syntaxHighlighter.ClassifiedSpans = classifiedSpans.ToHashSet();
		_syntaxHighlighter.RazorClassifiedSpans = razorClassifiedSpans.ToHashSet();
		Callable.From(() =>
		{
			_syntaxHighlighter.ClearHighlightingCache();
			//_syntaxHighlighter.UpdateCache();
			SyntaxHighlighter = null;
			SyntaxHighlighter = _syntaxHighlighter; // Reassign to trigger redraw
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

	private void OnCodeCompletionRequested()
	{
		var (caretLine, caretColumn) = GetCaretPosition();
		
		GD.Print($"Code completion requested at line {caretLine}, column {caretColumn}");
		_ = Task.GodotRun(async () =>
		{
			var linePos = new LinePosition(caretLine, caretColumn);
				
			var completions = await RoslynAnalysis.GetCodeCompletionsForDocumentAtPosition(_currentFile, linePos);
			await this.InvokeAsync(() =>
			{
				foreach (var completionItem in completions.ItemsList)
				{
					AddCodeCompletionOption(CodeCompletionKind.Class, completionItem.DisplayText, completionItem.DisplayText);
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