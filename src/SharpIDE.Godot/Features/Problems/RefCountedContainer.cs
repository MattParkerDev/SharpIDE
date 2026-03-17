using Godot;

namespace SharpIDE.Godot.Features.Problems;

public partial class RefCountedContainer<T>(T item) : RefCounted
{
    public T Item { get; } = item;
}

public partial class GodotObjectContainer<T>(T item) : GodotObject
{
    public T Item { get; } = item;
}