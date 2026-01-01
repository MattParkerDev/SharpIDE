using System.Diagnostics.CodeAnalysis;

using Godot;

using SharpIDE.Godot.Features.Tools;

namespace SharpIDE.Godot.Features.Layout;

public sealed record ToolMoveData(IdeToolId ToolId, ToolAnchor Anchor, int Index);

public partial class ToolDragOverlay : Control
{
	private readonly Dictionary<DropZone, ToolAnchor> _dropZoneAnchorMap = [];
	private readonly Dictionary<ToolAnchor, Sidebar> _sidebarMap = [];

	[Export]
	public Sidebar LeftSidebar { get; set; } = null!;

	[Export]
	public Sidebar RightSidebar { get; set; } = null!;

	[Export]
	public DropZone LeftTopZone { get; set; } = null!;

	[Export]
	public DropZone RightTopZone { get; set; } = null!;

	[Export]
	public DropZone BottomLeftZone { get; set; } = null!;

	[Export]
	public DropZone BottomRightZone { get; set; } = null!;

	public event EventHandler<ToolMoveData>? ToolMoveRequested;

	public override void _Ready()
	{
		_dropZoneAnchorMap[LeftTopZone] = ToolAnchor.LeftTop;
		_dropZoneAnchorMap[RightTopZone] = ToolAnchor.RightTop;
		_dropZoneAnchorMap[BottomLeftZone] = ToolAnchor.BottomLeft;
		_dropZoneAnchorMap[BottomRightZone] = ToolAnchor.BottomRight;

		_sidebarMap[ToolAnchor.LeftTop] = LeftSidebar;
		_sidebarMap[ToolAnchor.RightTop] = RightSidebar;
		_sidebarMap[ToolAnchor.BottomLeft] = LeftSidebar;
		_sidebarMap[ToolAnchor.BottomRight] = RightSidebar;

		VisibilityChanged += OnVisibilityChanged;
	}

	/// <inheritdoc />
	public override bool _CanDropData(Vector2 pos, Variant data)
	{
		if (data.VariantType is not Variant.Type.Int)
		{
			return false;
		}
		
		HideToolPreviews();
		HideDropZoneHighlights();

		var mousePosition = GetGlobalMousePosition();

		if (TryGetAnchorAndZoneAtPosition(mousePosition, out var anchor, out var dropZone))
		{
			ShowGhostPreview(anchor.Value, mousePosition);
			dropZone.Highlight.Show();
			return true;
		}

		return false;
	}

	/// <inheritdoc />
	public override void _DropData(Vector2 _, Variant data)
	{
		var mousePosition = GetGlobalMousePosition();

		if (TryGetAnchorAndZoneAtPosition(mousePosition, out var anchor, out var _))
		{
			RaiseToolDropped(
				data.As<IdeToolId>(),
				anchor.Value,
				CalculateInsertionIndex(GetSidebarTools(anchor.Value), mousePosition, preview: false));
		}
	}

	private void OnVisibilityChanged()
	{
		if (!Visible)
		{
			HideToolPreviews();
			HideDropZoneHighlights();
		}
	}

	private void RaiseToolDropped(IdeToolId toolId, ToolAnchor anchor, int index)
	{
		ToolMoveRequested?.Invoke(
			sender: this,
			new ToolMoveData(
				toolId,
				anchor,
				index));
	}

	private bool TryGetAnchorAndZoneAtPosition(
		Vector2 position,
		[NotNullWhen(true)] out ToolAnchor? anchor,
		[NotNullWhen(true)] out DropZone? dropZone)
	{
		anchor = null;
		dropZone = null;

		foreach (var (zone, zoneAnchor) in _dropZoneAnchorMap)
		{
			if (InDropZone(zone, position))
			{
				anchor = zoneAnchor;
				dropZone = zone;
				return true;
			}
		}

		return false;
	}

	private void ShowGhostPreview(ToolAnchor anchor, Vector2 mousePosition)
	{
		var sidebar = _sidebarMap[anchor];
		var tools = GetSidebarTools(anchor);
		var previewIndex = CalculateInsertionIndex(tools, mousePosition, preview: true);

		if (!ReferenceEquals(sidebar.ToolPreview.GetParent(), tools))
		{
			tools.AddChild(sidebar.ToolPreview);
		}

		tools.MoveChild(sidebar.ToolPreview, previewIndex);
	}

	private void HideToolPreviews()
	{
		if (LeftSidebar.ToolPreview.GetParent() is { } leftParent)
		{
			leftParent.RemoveChild(LeftSidebar.ToolPreview);
		}

		if (RightSidebar.ToolPreview.GetParent() is { } rightParent)
		{
			rightParent.RemoveChild(RightSidebar.ToolPreview);
		}
	}

	private void HideDropZoneHighlights()
	{
		foreach (var zone in _dropZoneAnchorMap.Keys)
		{
			zone.Highlight.Hide();
		}
	}

	private Container GetSidebarTools(ToolAnchor anchor)
	{
		var sidebar = _sidebarMap[anchor];

		return anchor switch
		{
			ToolAnchor.LeftTop or ToolAnchor.RightTop => sidebar.TopTools,
			ToolAnchor.BottomLeft or ToolAnchor.BottomRight => sidebar.BottomTools,
			_ => throw new ArgumentException($"No tools to show preview in for anchor '{anchor}'.", nameof(anchor))
		};
	}
	
	private static int CalculateInsertionIndex(Container tools, Vector2 mousePosition, bool preview)
	{
		var children = tools.GetChildren()
							.OfType<ToolButton>()

							// When in preview we don't ignore the disabled button (button being dragged)
							// in order to insert this preview in the correct index.
							// When calculating the index for the actual insertion we ignore the button being dragged.
							.Where(button => preview || !button.Disabled)
							.Index()
							.ToList();

		foreach (var (index, child) in children)
		{
			var rect = child.GetGlobalRect();
			var midpoint = rect.Position.Y + rect.Size.Y / 2.0f;

			if (mousePosition.Y < midpoint)
			{
				return index;
			}
		}

		return children.Count;
	}

	private static bool InDropZone(Control dropZone, Vector2 position)
	{
		return dropZone.GetGlobalRect().HasPoint(position);
	}
}
