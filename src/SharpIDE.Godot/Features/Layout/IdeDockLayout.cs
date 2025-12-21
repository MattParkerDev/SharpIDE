using Godot;

using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.CodeEditor;
using SharpIDE.Godot.Features.SolutionExplorer;

namespace SharpIDE.Godot.Features.Layout;

public partial class IdeDockLayout : Control
{
	private IdeLayoutNode _layout = null!;

	private SharpIdeSolutionModel? _solution;

	private IdeDockOverlay _dockOverlay = null!;

	private Control? _layoutNode;
	private IdeLayoutNode? _draggedNode;
	private readonly Dictionary<Control, IdeLayoutNode> _nodeMap = [];

	/// <inheritdoc />
	public override void _Ready()
	{
		_dockOverlay = GetNode<IdeDockOverlay>("%DockOverlay");

		// TODO: Add layout profiles and persist layout
		_layout = new IdeSplitNode(
			Orientation.Horizontal,
			FirstNode: new IdeSceneNode("uid://cy1bb32g7j7dr"),
			SecondNode: new IdeSplitNode(
				Orientation.Vertical,
				FirstNode: new IdeSceneNode("uid://cy1bb32g7j7dr"),
				SecondNode: new IdeSceneNode("uid://c5dlwgcx3ubyp")));

		FocusExited += CancelDrag;

		RebuildLayoutTree();
	}

	/// <inheritdoc />
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true })
		{
			CancelDrag();
		}
	}

	// TODO: This is currently a workaround. A better way to set the solution should be found.
	public void ChangeSolution(SharpIdeSolutionModel solution)
	{
		_solution = solution;

		CallDeferred(nameof(ChangeSolutionImpl));
	}

	private void ChangeSolutionImpl()
	{
		//if (_solution is null)
		{
			return;
		}

		foreach (var editor in GetAllChildren<CodeEditorPanel>(this))
		{
			editor.Solution = _solution;
		}

		foreach (var explorer in GetAllChildren<SolutionExplorerPanel>(this))
		{
			explorer.SolutionModel = _solution;
			_ = Task.GodotRun(explorer.BindToSolution);
		}
	}

	private IEnumerable<TNode> GetAllChildren<TNode>(Node node) where TNode : Node
	{
		return node.GetChildren()
				   .SelectMany(child =>
				   {
					   var children = GetAllChildren<TNode>(child);

					   if (child is TNode nodeChild)
					   {
						   children = children.Prepend(nodeChild);
					   }

					   return children;
				   });
	}

	private void RebuildLayoutTree()
	{
		_dockOverlay.ClearDockTargets();

		_layoutNode?.QueueFree();
		_layoutNode = BuildLayoutTree(_layout);
		AddChild(_layoutNode);

		ChangeSolution(_solution!);
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

	private (float FirstRation, float SecondRatio) GetStretchRatio(IdeSplitNode splitNode)
	{
		return (1.0f, 1.0f);
	}

	private Control BuildTabGroupLayout(IdeTabGroupNode tabGroupNode)
	{
		throw new NotImplementedException();
	}

	private Control BuildSceneLayout(IdeSceneNode sceneNode)
	{
		var sceneResource = ResourceLoader.Load<PackedScene>(sceneNode.ResourceUid);
		var scene = sceneResource.Instantiate<Control>();
		scene.GuiInput += input => OnSceneGuiInput(input, sceneNode);

		_dockOverlay.RegisterDockTarget(scene);
		_nodeMap[scene] = sceneNode;

		return scene;
	}

	private void OnSceneGuiInput(InputEvent input, IdeLayoutNode node)
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
		GD.Print(nameof(BeginDrag));
		
		_draggedNode = draggedNode;
		_dockOverlay.Visible = true;
	}

	private void EndDrag()
	{
		GD.Print(nameof(EndDrag));
		
		if (_draggedNode is null || _dockOverlay.CurrentDockPosition is DockPosition.None)
		{
			CancelDrag();
			return;
		}

		switch (_dockOverlay.CurrentDockScope)
		{
			case DockScope.Global:
				DockTarget(_draggedNode, _layout, _dockOverlay.CurrentDockPosition);
				break;
			case DockScope.Local when _dockOverlay.HoveredTarget is not null:
				DockTarget(_draggedNode, _nodeMap[_dockOverlay.HoveredTarget], _dockOverlay.CurrentDockPosition);
				break;
		}

		CancelDrag();
	}

	private void CancelDrag()
	{
		GD.Print(nameof(CancelDrag));
		
		_draggedNode = null;
		_dockOverlay.Visible = false;
	}

	private IdeLayoutNode ReplaceNode(IdeLayoutNode current, IdeLayoutNode target, IdeLayoutNode replacement)
	{
		// TODO: FIX
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

	private void DockTarget(IdeLayoutNode dragged, IdeLayoutNode target, DockPosition position)
	{
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

		_layout = ReplaceNode(RemoveNode(_layout, dragged)!, target, replacement);
		
		RebuildLayoutTree();
	}
}
