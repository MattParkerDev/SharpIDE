using System.Buffers;
using System.Text;
using GDExtensionBindgen;
using Godot;

namespace SharpIDE.Godot.Features.TerminalBase;

public partial class SharpIdeTerminal : Control
{
	private Terminal _terminal = null!;
	public override void _Ready()
	{
		var terminalControl = GetNode<Control>("Terminal");
		_terminal = new Terminal(terminalControl);
	}

	public void Write(string text)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		Write(bytes);
	}
	
	public void Write(byte[] text)
	{
		var (processedArray, length, wasRented) = ProcessLineEndings(text);
		try
		{
			_terminal.Write(processedArray.AsSpan(0, length));
		}
		finally
		{
			if (wasRented)
			{
				ArrayPool<byte>.Shared.Return(processedArray);
			}
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