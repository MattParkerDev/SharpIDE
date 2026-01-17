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

public abstract class VNode
{
	public readonly List<VNode> Children = [];

	public VNode this[params VNode[] children]
	{
		get
		{
			Children.AddRange(children);
			return this;
		}
	}

	// I believe requires abstract/override for runtime virtual dispatch regarding generics
	public abstract Node Build();

}


public sealed class VNode<T> : VNode where T : Node, new()
{
	public Action<T>? Configure { get; init; }

	public override Node Build()
	{
		var node = new T();
		Configure?.Invoke(node);

		foreach (var child in Children)
			node.AddChild(child.Build());

		return node;
	}
}

public static class VNodeExtensions
{
	public static VNode<T> _<T>(Action<T>? configure = null) where T : Node, new()
	{
		return new VNode<T>
		{
			Configure = configure
		};
	}
}
