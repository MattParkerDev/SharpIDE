using System.IO.Pipelines;
using GDExtensionBindgen;
using Godot;

namespace SharpIDE.Godot.Features.TerminalBase;

public partial class SharpIdeTerminal : Control
{
	private Terminal _terminal = null!;
	public PipeWriter? InputWriter { get; set; }
	public override void _Ready()
	{
		var terminalControl = GetNode<Control>("Terminal");
		_terminal = new Terminal(terminalControl);
		// TODO: Use lower level with no GC allocations, see GetBreakpointedLinesUnmanaged()
		_terminal.DataSent += async void (s) =>
		{
			var writer = InputWriter;
			if (writer is null) return;
			await writer.WriteAsync(s);
		};
	}

	public void Write(ReadOnlySpan<byte> text)
	{
		// need a buffer 2x the length of our span, as in theory each byte could be \n, requiring a new \r to accompany it
		Span<byte> workingBuffer = stackalloc byte[text.Length * 2];
		ProcessLineEndings(text, workingBuffer, out var processedArray);
		_terminal.Write(processedArray);
		_previousArrayEndedInCr = text.Length > 0 && text[^1] == (byte)'\r';
	}

	[RequiresGodotUiThread]
	public void ClearTerminal()
	{
		// .Clear removes all text except for the bottom row, so lets make sure we have a blank line, and cursor at start
		_terminal.Write("\r\n");
		_terminal.Clear();
	}
}
