using Godot;

namespace SharpIDE.Godot.Features.Problems;

public partial class RefCountedContainer(object? item) : RefCounted
{
    public object? Item { get; } = item;
}

public partial class GodotObjectContainer(object? item) : GodotObject
{
    public object? Item { get; } = item;
}
