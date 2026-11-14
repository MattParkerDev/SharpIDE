using Godot;
using Godot.NativeInterop;

namespace SharpIDE.Godot.Features.CodeEditor;

/// A line of a TextEdit, read as Godot's own native (UTF-32) String - so per-line work in hot paths (_Draw, find/count) doesn't allocate a managed string per line.
/// TextEdit stores each line as a ref counted String, so this is a ref count bump, not a copy. Indices into Span are Godot columns.
/// Must be disposed. UI thread only.
internal readonly ref struct TextEditNativeLine
{
	private readonly IntPtr _ptr;
	public ReadOnlySpan<uint> Span { get; }
	public int Length => Span.Length;

	private static bool _verifiedGetLineBinding;

	private unsafe TextEditNativeLine(IntPtr ptr)
	{
		_ptr = ptr;
		if (ptr == IntPtr.Zero)
		{
			Span = [];
			return;
		}
		var godotString = new godot_string { _ptr = ptr };
		var size = godotString.Size; // Includes the null terminator
		var length = size > 0 && ((uint*)ptr)[size - 1] == 0 ? size - 1 : size;
		Span = new ReadOnlySpan<uint>((uint*)ptr, length);
	}

	public static unsafe TextEditNativeLine Get(TextEdit textEdit, int line)
	{
		long lineArg = line; // Godot ints are 64 bit
		void** args = stackalloc void*[1] { &lineArg };
		godot_string ret = default; // A zeroed String is a valid empty String, which ptrcall assigns into
		NativeFuncs.godotsharp_method_bind_ptrcall(TextEdit.MethodBind44, GodotObject.GetPtr(textEdit), args, &ret);
		var result = new TextEditNativeLine(ret._ptr);
		if (_verifiedGetLineBinding is false) VerifyBinding(textEdit, line, result);
		return result;
	}

	public static int GetLength(TextEdit textEdit, int line)
	{
		using var nativeLine = Get(textEdit, line);
		return nativeLine.Length;
	}

	// MethodBind indices are generated per Godot version - fail loudly rather than read the wrong thing
	private static void VerifyBinding(TextEdit textEdit, int line, TextEditNativeLine nativeLine)
	{
		var managed = textEdit.GetLine(line);
		var expected = managed.EnumerateRunes().Select(r => (uint)r.Value).ToArray();
		if (nativeLine.Span.SequenceEqual(expected) is false)
		{
			nativeLine.Dispose();
			throw new InvalidOperationException("TextEdit.MethodBind44 is no longer get_line() - update it for this Godot version");
		}
		_verifiedGetLineBinding = true;
	}

	public void Dispose()
	{
		if (_ptr == IntPtr.Zero) return;
		var godotString = new godot_string { _ptr = _ptr };
		godotString.Dispose();
	}
}
