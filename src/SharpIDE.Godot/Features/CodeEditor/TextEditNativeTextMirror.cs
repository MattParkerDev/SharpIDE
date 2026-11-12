using System.Runtime.InteropServices;
using System.Text;
using Godot;
using Godot.NativeInterop;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Editor;

namespace SharpIDE.Godot.Features.CodeEditor;

/// Mirrors a TextEdit's text as Godot's own native (UTF-32) String, so each edit can be found without converting the whole document to a managed string.
/// get_text() returns a new, ref counted String that we hold one reference to until it is replaced or disposed - so it can't change or be freed underneath us.
/// UI thread only.
internal sealed class TextEditNativeTextMirror : IDisposable
{
	private static bool _verifiedGetTextBinding;
	private IntPtr _text; // godot_string._ptr of the last text read. We own this reference.
	private bool _disposed;

	/// Reads the TextEdit's text, without diffing, and returns it as a managed string (allocates the whole document - for loading).
	public string Reset(TextEdit textEdit)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Release(_text);
		_text = GetTextNative(textEdit);
		var text = Encoding.UTF32.GetString(MemoryMarshal.AsBytes(GetSpan(_text)));
		if (_verifiedGetTextBinding is false)
		{
			// MethodBind indices are generated per Godot version - fail loudly rather than mirror the wrong thing
			if (text != textEdit.Text) throw new InvalidOperationException("TextEdit.MethodBind39 is no longer get_text() - update it for this Godot version");
			_verifiedGetTextBinding = true;
		}
		return text;
	}

	/// Reads the TextEdit's text, and returns the change since the last read (UTF-16 positions, as Roslyn expects), or null if unchanged.
	public TextChange? Update(TextEdit textEdit)
	{
		if (_disposed) return null; // e.g. a late text_changed after the editor closed
		var newText = GetTextNative(textEdit);
		var change = TextDiff.GetMinimalChangeUtf32(GetSpan(_text), GetSpan(newText));
		if (change is null)
		{
			Release(newText);
			return null;
		}
		Release(_text);
		_text = newText;
		return change;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Release(_text);
		_text = IntPtr.Zero;
	}

	private static unsafe IntPtr GetTextNative(TextEdit textEdit)
	{
		godot_string ret = default; // A zeroed String is a valid empty String, which ptrcall assigns into
		NativeFuncs.godotsharp_method_bind_ptrcall(TextEdit.MethodBind39, GodotObject.GetPtr(textEdit), null, &ret);
		return ret._ptr;
	}

	private static unsafe ReadOnlySpan<uint> GetSpan(IntPtr text)
	{
		if (text == IntPtr.Zero) return [];
		var godotString = new godot_string { _ptr = text };
		var size = godotString.Size; // Includes the null terminator
		var length = size > 0 && ((uint*)text)[size - 1] == 0 ? size - 1 : size;
		return new ReadOnlySpan<uint>((uint*)text, length);
	}

	private static void Release(IntPtr text)
	{
		if (text == IntPtr.Zero) return;
		var godotString = new godot_string { _ptr = text };
		godotString.Dispose();
	}
}
