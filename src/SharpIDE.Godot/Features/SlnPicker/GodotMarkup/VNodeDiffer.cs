using BlazorGodot.Library.V2;

namespace SharpIDE.Godot.Features.SlnPicker.GodotMarkup;

public static class VNodeDiffer
{
	public static List<DiffOperation> ComputeDiff(
		VNode?  oldVNode,
		VNode newVNode,
		NodeMapping mapping)
	{
		var operations = new List<DiffOperation>();
		
		// Root level diff
		if (oldVNode == null)
		{
			operations.Add(DiffOperation.Insert(0, newVNode));
			return operations;
		}

		// Assign sequences to new tree
		AssignSequences(newVNode, 0);
		
		DiffNode(oldVNode, newVNode, mapping, operations, 0);
		
		return operations;
	}

	private static int AssignSequences(VNode vNode, int sequence)
	{
		vNode.Sequence = sequence++;
		foreach (var child in vNode.Children)
		{
			sequence = AssignSequences(child, sequence);
		}
		return sequence;
	}

	private static void DiffNode(
		VNode oldVNode,
		VNode newVNode,
		NodeMapping mapping,
		List<DiffOperation> operations,
		int siblingIndex)
	{
		// Different node types = replace
		if (oldVNode. NodeType != newVNode.NodeType)
		{
			var oldNode = mapping.GetNode(oldVNode);
			if (oldNode != null)
			{
				operations.Add(DiffOperation.Remove(siblingIndex, oldNode));
				mapping.Unmap(oldVNode);
			}
			operations.Add(DiffOperation.Insert(siblingIndex, newVNode));
			return;
		}

		// Same type = update and diff children
		var node = mapping.GetNode(oldVNode);
		if (node != null)
		{
			operations.Add(DiffOperation.Update(siblingIndex, newVNode, node));
			mapping. Unmap(oldVNode);
			mapping.Map(newVNode, node);
		}

		// Diff children
		DiffChildren(oldVNode. Children, newVNode.Children, mapping, operations);
	}

	private static void DiffChildren(
		List<VNode> oldChildren,
		List<VNode> newChildren,
		NodeMapping mapping,
		List<DiffOperation> operations)
	{
		// Check if any children have keys
		var hasKeys = oldChildren.Any(c => c.Key != null) || newChildren.Any(c => c.Key != null);

		if (hasKeys)
		{
			DiffChildrenByKey(oldChildren, newChildren, mapping, operations);
		}
		else
		{
			DiffChildrenBySequence(oldChildren, newChildren, mapping, operations);
		}
	}

	private static void DiffChildrenBySequence(
		List<VNode> oldChildren,
		List<VNode> newChildren,
		NodeMapping mapping,
		List<DiffOperation> operations)
	{
		int oldIndex = 0, newIndex = 0;
		int siblingIndex = 0;

		// Process common prefix
		while (oldIndex < oldChildren.Count && newIndex < newChildren.Count)
		{
			var oldChild = oldChildren[oldIndex];
			var newChild = newChildren[newIndex];

			DiffNode(oldChild, newChild, mapping, operations, siblingIndex);

			oldIndex++;
			newIndex++;
			siblingIndex++;
		}

		// Remove remaining old children
		while (oldIndex < oldChildren.Count)
		{
			var oldChild = oldChildren[oldIndex];
			var oldNode = mapping.GetNode(oldChild);
			if (oldNode != null)
			{
				operations.Add(DiffOperation. Remove(siblingIndex, oldNode));
				mapping.Unmap(oldChild);
			}
			oldIndex++;
		}

		// Insert remaining new children
		while (newIndex < newChildren.Count)
		{
			var newChild = newChildren[newIndex];
			operations.Add(DiffOperation.Insert(siblingIndex, newChild));
			newIndex++;
			siblingIndex++;
		}
	}

	private static void DiffChildrenByKey(
		List<VNode> oldChildren,
		List<VNode> newChildren,
		NodeMapping mapping,
		List<DiffOperation> operations)
	{
		// Build lookup dictionaries
		var oldByKey = oldChildren
			.Where(c => c. Key != null)
			.ToDictionary(c => c.Key!, c => c);
		
		var newByKey = newChildren
			.Where(c => c.Key != null)
			.ToDictionary(c => c. Key!, c => c);

		var processedOld = new HashSet<VNode>();
		int siblingIndex = 0;

		// Process new children
		foreach (var newChild in newChildren)
		{
			if (newChild.Key != null && oldByKey.TryGetValue(newChild.Key, out var oldChild))
			{
				// Found matching key
				DiffNode(oldChild, newChild, mapping, operations, siblingIndex);
				processedOld.Add(oldChild);
			}
			else
			{
				// New child without match
				operations.Add(DiffOperation.Insert(siblingIndex, newChild));
			}
			
			siblingIndex++;
		}

		// Remove unmatched old children
		foreach (var oldChild in oldChildren)
		{
			if (!processedOld.Contains(oldChild))
			{
				var oldNode = mapping.GetNode(oldChild);
				if (oldNode != null)
				{
					operations.Add(DiffOperation.Remove(0, oldNode)); // Will be removed from wherever it is
					mapping.Unmap(oldChild);
				}
			}
		}
	}
}