using Godot;

namespace SharpIDE.Godot.Features.SlnPicker.GodotMarkup;

public class NodeMapping
{
    private readonly Dictionary<VNode, Node> _vNodeToNode = new();
    private readonly Dictionary<Node, VNode> _nodeToVNode = new();

    public void Map(VNode vNode, Node node)
    {
        _vNodeToNode[vNode] = node;
        _nodeToVNode[node] = vNode;
    }

    public Node?  GetNode(VNode vNode) => _vNodeToNode.GetValueOrDefault(vNode);
    public VNode? GetVNode(Node node) => _nodeToVNode.GetValueOrDefault(node);

    public void Unmap(VNode vNode)
    {
        if (_vNodeToNode.TryGetValue(vNode, out var node))
        {
            _vNodeToNode.Remove(vNode);
            _nodeToVNode.Remove(node);
        }
    }

    public void Clear()
    {
        _vNodeToNode.Clear();
        _nodeToVNode.Clear();
    }
}