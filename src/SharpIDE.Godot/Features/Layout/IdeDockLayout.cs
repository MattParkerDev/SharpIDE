using Godot;

using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Layout;

public partial class IdeDockLayout : Control
{
	[Export]
	public int DockComponentSeparation { get; set; } = 0;
	
	private readonly Dictionary<Control, IdeLayoutNode> _nodeMap = [];
	private IdeLayoutNode _layout = null!;

	private IdeDockOverlay _dockOverlay = null!;

	private Control? _layoutNode;

	/// <inheritdoc />
	public override void _Ready()
	{
		_dockOverlay = GetNode<IdeDockOverlay>("%DockOverlay");

		FocusExited += _dockOverlay.EndDrag;
	}

	/// <inheritdoc />
	public override void _UnhandledKeyInput(InputEvent input)
	{
		if (input is InputEventKey { Keycode: Key.Escape, Pressed: true })
		{
			_dockOverlay.EndDrag();
		}
	}

	public void UpdateLayout(IdeLayoutNode layout)
	{
		_layout = layout;
		RebuildLayoutTree();
	}
	
	private void RebuildLayoutTree()
	{
		_dockOverlay.ClearDockTargets();

		_layoutNode?.QueueFree();
		_layoutNode = BuildLayoutTree(_layout);
		AddChild(_layoutNode);
	}

	private Control BuildLayoutTree(IdeLayoutNode layoutNode)
	{
		return layoutNode switch
		{
			IdeSplitNode splitNode => BuildSplitLayout(splitNode),
			IdeTabGroupNode tabGroupNode => BuildTabGroupLayout(tabGroupNode),
			IdeSceneNode viewNode => BuildSceneLayout(viewNode),

			_ => throw new ArgumentException(
					 $"The layout node of type '{layoutNode.GetType().FullName}' is not supported.",
					 nameof(layoutNode))
		};
	}

	private Control BuildSplitLayout(IdeSplitNode splitNode)
	{
		SplitContainer container = splitNode.Orientation switch
		{
			Orientation.Horizontal => new HSplitContainer(),
			Orientation.Vertical => new VSplitContainer(),

			_ => throw new ArgumentException(
					 $"The split orientation '{splitNode.Orientation}' is not supported.",
					 nameof(splitNode))
		};

		container.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		container.SizeFlagsVertical = SizeFlags.ExpandFill;
		container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		container.AddThemeConstantOverride("separation", DockComponentSeparation);

		var firstChild = BuildLayoutTree(splitNode.FirstNode);
		var secondChild = BuildLayoutTree(splitNode.SecondNode);

		container.AddChild(firstChild);
		container.AddChild(secondChild);

		var (firstRatio, secondRatio) = GetStretchRatio(splitNode);

		firstChild.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		firstChild.SizeFlagsVertical = SizeFlags.ExpandFill;
		secondChild.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		secondChild.SizeFlagsVertical = SizeFlags.ExpandFill;
		firstChild.SetStretchRatio(firstRatio);
		secondChild.SetStretchRatio(secondRatio);

		_nodeMap[container] = splitNode;

		return container;
	}

	private (float FirstRatio, float SecondRatio) GetStretchRatio(IdeSplitNode splitNode)
	{
		// TODO: Calculate ratio
		return (1.0f, 1.0f);
	}

	private Control BuildTabGroupLayout(IdeTabGroupNode tabGroupNode)
	{
		throw new NotImplementedException();
	}

	private Control BuildSceneLayout(IdeSceneNode sceneNode)
	{
		var component = ResourceLoader.Load<PackedScene>("uid://b36lno754a2is").Instantiate<IdeDockComponent>();
		component.ComponentNode = sceneNode;
		component.GuiInput += input => OnDockComponentGuiInput(input, sceneNode);

		_dockOverlay.RegisterDockTarget(component);
		_nodeMap[component] = sceneNode;

		return component;
	}

	private void OnDockComponentGuiInput(InputEvent input, IdeLayoutNode node)
	{
		if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } leftButton)
		{
			return;
		}

		if (leftButton.Pressed)
		{
			BeginDrag(node);
			return;
		}

		EndDrag();
	}

	private void BeginDrag(IdeLayoutNode draggedNode)
	{
		_dockOverlay.BeginDrag(draggedNode);
	}

	private void EndDrag()
	{
		if (_dockOverlay.DraggedNode is null || _dockOverlay.CurrentDockPosition is DockPosition.None)
		{
			_dockOverlay.EndDrag();
			return;
		}

		switch (_dockOverlay.CurrentDockScope)
		{
			case DockScope.Global:
				DockTarget(_dockOverlay.DraggedNode, _layout, _dockOverlay.CurrentDockPosition);
				break;
			case DockScope.Local when _dockOverlay.HoveredTarget is not null:
				DockTarget(_dockOverlay.DraggedNode, _nodeMap[_dockOverlay.HoveredTarget], _dockOverlay.CurrentDockPosition);
				break;
		}

		_dockOverlay.EndDrag();
	}

	private void DockTarget(IdeLayoutNode dragged, IdeLayoutNode target, DockPosition position)
	{
		if (ReferenceEquals(_layout, target))
		{
			_layout = RemoveNode(target, dragged)!;
			target = _layout;
		}
		else
		{
			_layout = RemoveNode(_layout, dragged)!;
			target = RemoveNode(target, dragged)!;
		}
		
		
		var replacement = position switch
		{
			DockPosition.Left => new IdeSplitNode(Orientation.Horizontal, dragged, target),
			DockPosition.Right => new IdeSplitNode(Orientation.Horizontal, target, dragged),
			DockPosition.Top => new IdeSplitNode(Orientation.Vertical, dragged, target),
			DockPosition.Bottom => new IdeSplitNode(Orientation.Vertical, target, dragged),

			_ => target
		};

		if (ReferenceEquals(replacement, target))
		{
			return;
		}

		_layout = ReplaceNode(_layout, target, replacement);

		RebuildLayoutTree();
	}

	private IdeLayoutNode ReplaceNode(IdeLayoutNode current, IdeLayoutNode target, IdeLayoutNode replacement)
	{
		if (ReferenceEquals(_layout, target) || ReferenceEquals(current, target))
		{
			return replacement;
		}

		if (current is IdeSplitNode splitNode)
		{
			return splitNode with
			{
				FirstNode = ReplaceNode(splitNode.FirstNode, target, replacement),
				SecondNode = ReplaceNode(splitNode.SecondNode, target, replacement)
			};
		}

		return current;
	}

	private IdeLayoutNode? RemoveNode(IdeLayoutNode current, IdeLayoutNode target)
	{
		if (ReferenceEquals(current, target))
		{
			return null;
		}

		if (current is not IdeSplitNode splitNode)
		{
			return current;
		}

		var first = RemoveNode(splitNode.FirstNode, target);
		var second = RemoveNode(splitNode.SecondNode, target);

		if (first is null)
		{
			return second;
		}

		if (second is null)
		{
			return first;
		}

		return splitNode with
		{
			FirstNode = first,
			SecondNode = second
		};
	}
}
