using Godot.NativeInterop;

namespace SharpIDE.Godot.Features.Common;

public readonly ref struct GodotNativePackedInt32ArrayHandle<T> where T : unmanaged
{
	public ReadOnlySpan<T> Span { get; init; }
	public godot_packed_int32_array NativePackedInt32Array { get; init; }

	public void Dispose()
	{
		NativePackedInt32Array.Dispose();
	}
}
