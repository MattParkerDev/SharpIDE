using Godot;
using Godot.NativeInterop;
using SharpIDE.Godot.Features.Common;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	/// <summary>
	/// You must dispose this via .Dispose or a using statement to free the unmanaged memory 
	/// </summary>
	private GodotNativePackedInt32ArrayHandle<int> GetBreakpointedLinesUnmanaged()
	{
		unsafe
		{
			var packedInt32Array = new godot_packed_int32_array();
			// GetBreakpointedLines
			NativeFuncs.godotsharp_method_bind_ptrcall(CodeEdit.MethodBind31, GodotObject.GetPtr(this), null, &packedInt32Array);
			//var packedInt32Array = NativeFuncs.godotsharp_variant_as_packed_int32_array(in nativeVariant); // if it was returned as a struct rather than ptr, most likely via godotsharp_method_bind_call
			var span = new Span<int>(packedInt32Array.Buffer, packedInt32Array.Size);
			var godotNativePackedInt32ArrayHandle = new GodotNativePackedInt32ArrayHandle<int>
			{
				Span = span,
				NativePackedInt32Array = packedInt32Array
			};
			return godotNativePackedInt32ArrayHandle;
		}
	}
}
