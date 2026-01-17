using Godot;

namespace SharpIDE.Godot.Features.SlnPicker.GodotMarkup;

public abstract class VNode
{
    public readonly List<VNode> Children = [];
    public object? Key { get; init; }  // For keyed diffing
    internal int Sequence { get; set; }  // Auto-assigned sequence number
	
    public VNode this[params ReadOnlySpan<VNode> children]
    {
        get
        {
            Children.AddRange(children);
            return this;
        }
    }
    public VNode this[IEnumerable<VNode> children]
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

// Enhanced VNode with diffing support

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