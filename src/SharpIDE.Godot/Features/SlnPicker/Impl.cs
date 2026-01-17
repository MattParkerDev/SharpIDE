using Godot;
using static BlazorGodot.Library.V2.VNodeExtensions;

namespace BlazorGodot.Library.V2;

public partial class TestComp : Node
{
	private List<string> _myStrings = ["one", "two", "three"];

	protected VNode Render() =>
		_<VBoxContainer>()
		[
			_<Label>(s => { s.Text = "Hello, World!"; s.TextDirection = Control.TextDirection.Auto; })
			[[
				_<Label>(),
				.. _myStrings.Select(s => _<Label>(l => l.Text = s))
			]],
			_<Label>()
		];

	public override void _Ready()
	{
		var root = Render().Build();
		AddChild(root);
	}
}

// Enhanced VNode with diffing support
public abstract class VNode
{
	public readonly List<VNode> Children = [];
	public object? Key { get; init; }  // For keyed diffing
	internal int Sequence { get; set; }  // Auto-assigned sequence number
	
	public VNode this[params VNode[] children]
	{
		get
		{
			Children.AddRange(children);
			return this;
		}
	}

	public abstract Node Build();
	public abstract Type NodeType { get; }
	
	// Apply configuration to existing node (for updates)
	public abstract void Configure(Node node);
}

public sealed class VNode<T> : VNode where T : Node, new()
{
	public Action<T>? ConfigureAction { get; init; }
	
	public override Type NodeType => typeof(T);

	public override Node Build()
	{
		var node = new T();
		ConfigureAction?.Invoke(node);

		foreach (var child in Children)
			node.AddChild(child.Build());

		return node;
	}

	public override void Configure(Node node)
	{
		if (node is T typedNode)
			ConfigureAction?.Invoke(typedNode);
	}
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

// Tracks mapping between VNodes and actual Godot Nodes
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

public static class VNodeExtensions
{
	public static VNode<T> _<T>(Action<T>? configure = null) where T : Node, new()
	{
		return new VNode<T>
		{
			ConfigureAction = configure
		};
	}

	// Keyed version
	public static VNode<T> _<T>(object key, Action<T>? configure = null) where T : Node, new()
	{
		return new VNode<T>
		{
			Key = key,
			ConfigureAction = configure
		};
	}
}