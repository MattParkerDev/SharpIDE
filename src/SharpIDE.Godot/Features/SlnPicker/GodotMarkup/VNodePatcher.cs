using BlazorGodot.Library.V2;
using Godot;

namespace SharpIDE.Godot.Features.SlnPicker.GodotMarkup;

public static class VNodePatcher
{
    public static void ApplyDiff(
        Node parentNode,
        List<DiffOperation> operations,
        NodeMapping mapping)
    {
        foreach (var op in operations)
        {
            switch (op.Action)
            {
                case DiffActionType.Insert:
                    ApplyInsert(parentNode, op, mapping);
                    break;
				
                case DiffActionType.Remove:
                    ApplyRemove(parentNode, op);
                    break;
				
                case DiffActionType.Update:
                    ApplyUpdate(op);
                    break;
				
                case DiffActionType.Move:
                    ApplyMove(parentNode, op);
                    break;
            }
        }
    }

    private static void ApplyInsert(Node parentNode, DiffOperation op, NodeMapping mapping)
    {
        if (op.VNode == null) return;

        var newNode = BuildNodeTree(op.VNode, mapping);
		
        // Insert at specific index
        if (op.SiblingIndex < parentNode.GetChildCount())
        {
            parentNode.AddChild(newNode);
            parentNode.MoveChild(newNode, op.SiblingIndex);
        }
        else
        {
            parentNode.AddChild(newNode);
        }
    }

    private static void ApplyRemove(Node parentNode, DiffOperation op)
    {
        if (op.OldNode == null) return;

        parentNode.RemoveChild(op.OldNode);
        op.OldNode. QueueFree();
    }

    private static void ApplyUpdate(DiffOperation op)
    {
        if (op.VNode == null || op.OldNode == null) return;

        // Reconfigure the node with new properties
        op.VNode.Configure(op.OldNode);
    }

    private static void ApplyMove(Node parentNode, DiffOperation op)
    {
        if (op.OldNode == null || ! op.MoveToIndex.HasValue) return;

        parentNode. MoveChild(op.OldNode, op.MoveToIndex.Value);
    }

    private static Node BuildNodeTree(VNode vNode, NodeMapping mapping)
    {
        var node = vNode.Build();
        mapping.Map(vNode, node);
		
        // Map all children recursively
        MapDescendants(vNode, node, mapping);
		
        return node;
    }

    private static void MapDescendants(VNode vNode, Node node, NodeMapping mapping)
    {
        for (int i = 0; i < vNode.Children.Count; i++)
        {
            var childVNode = vNode.Children[i];
            var childNode = node.GetChild(i);
            mapping.Map(childVNode, childNode);
            MapDescendants(childVNode, childNode, mapping);
        }
    }
}