using Godot;

namespace SharpIDE.Godot.Features.SlnPicker.GodotMarkup;

public readonly struct DiffOperation
{
    public DiffActionType Action { get; init; }
    public int SiblingIndex { get; init; }
    public VNode?  VNode { get; init; }
    public Node? OldNode { get; init; }
    public int?  MoveToIndex { get; init; }  // For move operations

    public static DiffOperation Insert(int siblingIndex, VNode vNode) =>
        new() { Action = DiffActionType. Insert, SiblingIndex = siblingIndex, VNode = vNode };

    public static DiffOperation Remove(int siblingIndex, Node oldNode) =>
        new() { Action = DiffActionType. Remove, SiblingIndex = siblingIndex, OldNode = oldNode };

    public static DiffOperation Update(int siblingIndex, VNode vNode, Node oldNode) =>
        new() { Action = DiffActionType.Update, SiblingIndex = siblingIndex, VNode = vNode, OldNode = oldNode };

    public static DiffOperation Move(int fromIndex, int toIndex, Node node) =>
        new() { Action = DiffActionType.Move, SiblingIndex = fromIndex, MoveToIndex = toIndex, OldNode = node };
}

// Diff operations
public enum DiffActionType
{
    None,
    Insert,
    Remove,
    Update,
    Move
}