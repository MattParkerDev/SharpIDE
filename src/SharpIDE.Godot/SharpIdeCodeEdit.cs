using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Godot;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using Task = System.Threading.Tasks.Task;

namespace SharpIDE.Godot;

public partial class SharpIdeCodeEdit : CodeEdit
{
	[Signal]
	public delegate void CodeFixesRequestedEventHandler();

	private int _currentLine;
	private int _selectionStartCol;
	private int _selectionEndCol;
	
	private CustomHighlighter _syntaxHighlighter = new();
	private PopupMenu _popupMenu = null!;

	private ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)> _diagnostics = [];
	
	public override void _Ready()
	{
		_popupMenu = GetNode<PopupMenu>("CodeFixesMenu");
		_popupMenu.IdPressed += (id) =>
		{
			GD.Print($"Code fix selected: {id}");
		};
		this.CodeCompletionRequested += OnCodeCompletionRequested;
		this.CodeFixesRequested += OnCodeFixesRequested;
		this.CaretChanged += () =>
		{
			_selectionStartCol = GetSelectionFromColumn();
			_selectionEndCol = GetSelectionToColumn();
			_currentLine = GetCaretLine();
			GD.Print($"Selection changed to line {_currentLine}, start {_selectionStartCol}, end {_selectionEndCol}");
		};
		this.SyntaxHighlighter = _syntaxHighlighter;
	}
	
	public void UnderlineRange(int line, int caretStartCol, int caretEndCol, Color color, float thickness = 1.5f)
	{
		if (line < 0 || line >= GetLineCount())
			return;

		if (caretStartCol >= caretEndCol) // nothing to draw
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
		DrawDashedLine(startPos, endPos, color, thickness);
		//DrawLine(startPos, endPos, color, thickness);
	}
	public override void _Draw()
	{
		UnderlineRange(_currentLine, _selectionStartCol, _selectionEndCol, new Color(1, 0, 0));
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
	}

	public void ProvideDiagnostics(ImmutableArray<(FileLinePositionSpan fileSpan, Diagnostic diagnostic)> diagnostics)
	{
		_diagnostics = diagnostics;
	}
	public void ProvideSyntaxHighlighting(IEnumerable<(FileLinePositionSpan fileSpan, ClassifiedSpan classifiedSpan)> classifiedSpans)
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
		var test = GetCaretDrawPos();
		_popupMenu.Position = new Vector2I((int)test.X, (int)test.Y);
		_popupMenu.Clear();
		_popupMenu.AddItem("Getting Context Actions...", 0);
		_popupMenu.Popup();
		GD.Print($"Code fixes requested at line {caretLine}, column {caretColumn}");
		_ = Task.Run(async () =>
		{
			try
			{
				var linePos = new LinePosition(caretLine, caretColumn);
				var diagnostic = _diagnostics.FirstOrDefault(d =>
					d.fileSpan.StartLinePosition <= linePos && d.fileSpan.EndLinePosition >= linePos);
				if (diagnostic is (_, null)) return;
				var codeActions = await RoslynAnalysis.GetCodeFixesAsync(diagnostic.diagnostic);
				Callable.From(() =>
				{
					_popupMenu.Clear();
					foreach (var (index, (fileSpan, codeAction)) in codeActions.Index())
					{
						_popupMenu.AddItem(codeAction.Title, index);
						//_popupMenu.SetItemMetadata(menuItem, codeAction);
					}
					GD.Print($"Code fixes found: {codeActions.Length}, displaying menu");
				}).CallDeferred();
			}
			catch (Exception ex)
			{
				GD.Print(ex);
			}
		});
	}

	private void OnCodeCompletionRequested()
	{
		var (caretLine, caretColumn) = GetCaretPosition();
		GD.Print($"Code completion requested at line {caretLine}, column {caretColumn}");
	}
	
	private (int, int) GetCaretPosition()
	{
		var caretColumn = GetCaretColumn();
		var caretLine = GetCaretLine();
		return (caretLine, caretColumn);
	}
}