using GDExtensionBindgen;
using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.TerminalBase;

public partial class SharpIdeTerminal : Control
{
	private Terminal _terminal = null!;
	// Written by AttachProcessIo (UI thread) and DetachProcessIo (any thread), read on the UI thread
	private StandardIo? _attachedIo;

	public override void _Ready()
	{
		var terminalControl = GetNode<Control>("Terminal");
		_terminal = new Terminal(terminalControl);
		_terminal.DataSent += OnTerminalDataSent;
		_terminal.SizeChanged += OnTerminalSizeChanged;
	}

	/// Connects this terminal to a process' standard IO: input typed in the terminal is written to the process,
	/// and if the process is attached to a PTY, the PTY is kept the same size as this terminal
	[RequiresGodotUiThread]
	public void AttachProcessIo(StandardIo io)
	{
		Volatile.Write(ref _attachedIo, io);
		io.ResizeTerminal(_terminal.GetCols(), _terminal.GetRows());
	}

	/// Disconnects <paramref name="io"/>, if it is still the attached IO - a late detach from a previous run will not detach a newer one
	public void DetachProcessIo(StandardIo io)
	{
		Interlocked.CompareExchange(ref _attachedIo, null, io);
	}

	// TODO: Use lower level with no GC allocations, see GetBreakpointedLinesUnmanaged()
	private async void OnTerminalDataSent(byte[] data)
	{
		var writer = Volatile.Read(ref _attachedIo)?.StdinWriter;
		if (writer is null) return;
		try
		{
			await writer.WriteAsync(data);
		}
		catch (InvalidOperationException)
		{
			// The output pump completed the writer (process exited) between us reading it and writing to it - the input has nowhere to go
		}
	}

	private void OnTerminalSizeChanged(Vector2I newSize)
	{
		Volatile.Read(ref _attachedIo)?.ResizeTerminal(newSize.X, newSize.Y);
	}

	/// <param name="translateLineEndings">Replace lone \n with \r\n. Pass false for output from a PTY, which has already done so</param>
	public void Write(ReadOnlySpan<byte> text, bool translateLineEndings = true)
	{
		if (translateLineEndings is false)
		{
			// Godot currently can't cast a ReadOnlySpan<T> to Variant, only Span<T>
			Span<byte> buffer = stackalloc byte[text.Length];
			text.CopyTo(buffer);
			_terminal.Write(buffer);
		}
		else
		{
			// need a buffer 2x the length of our span, as in theory each byte could be \n, requiring a new \r to accompany it
			Span<byte> workingBuffer = stackalloc byte[text.Length * 2];
			ProcessLineEndings(text, workingBuffer, out var processedArray);
			_terminal.Write(processedArray);
		}
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
