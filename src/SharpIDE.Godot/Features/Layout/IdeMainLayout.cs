using Godot;

using SharpIDE.Godot.Features.Layout.Resources;
using SharpIDE.Godot.Features.Tools;

namespace SharpIDE.Godot.Features.Layout;

public partial class IdeMainLayout : Control
{
	private readonly Dictionary<ToolAnchor, AnchorState> _anchorStateMap = [];
	private readonly Dictionary<IdeToolId, ToolButton> _toolButtonMap = [];

	[Inject]
	private readonly SharpIdeToolManager _toolManager = null!;

	private Dictionary<IdeToolId, IdeToolState> _toolStateMap = [];

	private Sidebar _leftSidebar = null!;
	private Sidebar _rightSidebar = null!;
	private Control _bottomArea = null!;
	private ToolDragOverlay _toolDragOverlay = null!;
	private IdeLayoutState _layout = null!;

	public override void _Ready()
	{
		_leftSidebar = GetNode<Sidebar>("%LeftSidebar");
		_rightSidebar = GetNode<Sidebar>("%RightSidebar");
		_bottomArea = GetNode<Control>("%BottomArea");
		_toolDragOverlay = GetNode<ToolDragOverlay>("%ToolDragOverlay");

		_anchorStateMap[ToolAnchor.LeftTop] = new AnchorState(
			_leftSidebar.TopTools,
			GetNode<ToolArea>("%LeftTopToolArea"),
			new ButtonGroup { AllowUnpress = true });
		_anchorStateMap[ToolAnchor.RightTop] = new AnchorState(
			_rightSidebar.TopTools,
			GetNode<ToolArea>("%RightTopToolArea"),
			new ButtonGroup { AllowUnpress = true });
		_anchorStateMap[ToolAnchor.BottomLeft] = new AnchorState(
			_leftSidebar.BottomTools,
			GetNode<ToolArea>("%BottomLeftToolArea"),
			new ButtonGroup { AllowUnpress = true });
		_anchorStateMap[ToolAnchor.BottomRight] = new AnchorState(
			_rightSidebar.BottomTools,
			GetNode<ToolArea>("%BottomRightToolArea"),
			new ButtonGroup { AllowUnpress = true });

		// TODO: Load layout from persistence
		_layout = IdeLayoutState.Default;
		
		InitializeLayout(_layout);

		GodotGlobalEvents.Instance.IdeToolExternallyActivated.Subscribe(tool =>
		{
			CallDeferred(
				nameof(OnIdeToolExternallyActivated),
				Variant.From(tool));
			return Task.CompletedTask;
		});

		_toolDragOverlay.ToolMoveRequested += OnToolMoveRequested;
	}

	/// <inheritdoc />
	public override void _Notification(int what)
	{
		switch ((long)what)
		{
			case NotificationDragBegin:
				_leftSidebar.Visible = true;
				_rightSidebar.Visible = true;
				_toolDragOverlay.Visible = true;
				break;
			case NotificationDragEnd:
				_toolDragOverlay.Visible = false;
				ApplySidebarVisibility();
				break;
		}
	}

	private void OnIdeToolExternallyActivated(IdeToolId toolId)
	{
		if (!_toolStateMap.TryGetValue(toolId, out var toolState))
		{
			GD.PrintErr($"Externally activated tool '{toolId}' is not part of layout.");
			return;
		}

		var anchor = toolState.Anchor;
		DeactivateTools(anchor);
		ActivateTool(toolId);
	}

	private void OnToolMoveRequested(object? _, ToolMoveData moveData)
	{
		MoveTool(moveData.ToolId, moveData.Anchor, moveData.Index);
	}

	private void InitializeLayout(IdeLayoutState layoutState)
	{
		_toolStateMap = layoutState.SidebarTools.Values.SelectMany(x => x).ToDictionary(state => state.ToolId);

		foreach (var anchorState in _anchorStateMap.Values)
		{
			anchorState.SidebarTools.RemoveChildren();
		}

		foreach (var toolState in _toolStateMap.Values)
		{
			var anchorState = _anchorStateMap[toolState.Anchor];

			var button = CreateToolButton(toolState);
			_toolButtonMap[toolState.ToolId] = button;

			anchorState.SidebarTools.AddChild(button);

			if (toolState.IsActive)
			{
				var toolControl = _toolManager.GetControl<Control>(toolState.ToolId);

				anchorState.ToolArea.ShowTool(toolControl);
			}
		}

		ApplySidebarVisibility();
		ApplyBottomAreaVisibility();
	}

	private void ActivateTool(IdeToolId toolId)
	{
		SetToolActive(toolId, true);
		_toolButtonMap[toolId].SetPressedNoSignal(true);
		ApplyToolVisibility(toolId);
	}

	private void DeactivateTool(IdeToolId toolId)
	{
		SetToolActive(toolId, false);
		_toolButtonMap[toolId].SetPressedNoSignal(false);
		ApplyToolVisibility(toolId);
	}

	private void DeactivateTools(ToolAnchor anchor)
	{
		foreach (var toolState in _toolStateMap.Values.Where(t => t.Anchor == anchor))
		{
			DeactivateTool(toolState.ToolId);
		}
	}

	private void MoveTool(IdeToolId toolId, ToolAnchor targetAnchor, int anchorToolIndex)
	{
		var toolState = _toolStateMap[toolId];
		var targetAnchorState = _anchorStateMap[targetAnchor];
		var toolButton = _toolButtonMap[toolState.ToolId];

		var originAnchor = toolState.Anchor;
		var originAnchorState = _anchorStateMap[originAnchor];

		_layout.SidebarTools[toolState.Anchor].Remove(toolState);
		_layout.SidebarTools[targetAnchor].Insert(anchorToolIndex, toolState);
		toolState.Anchor = targetAnchor;

		if (originAnchor == targetAnchor)
		{
			targetAnchorState.SidebarTools.MoveChild(toolButton, anchorToolIndex);
			return;
		}

		if (toolButton.GetParent() is { } buttonParent)
		{
			buttonParent.RemoveChild(toolButton);
		}

		var toolControl = _toolManager.GetControl<Control>(toolId);

		if (ReferenceEquals(originAnchorState.ToolArea.CurrentTool, toolControl))
		{
			originAnchorState.ToolArea.HideTool();
		}

		toolButton.ButtonGroup = targetAnchorState.ButtonGroup;
		targetAnchorState.SidebarTools.AddChild(toolButton);
		targetAnchorState.SidebarTools.MoveChild(toolButton, anchorToolIndex);

		if (toolButton.ButtonPressed)
		{
			OnIdeToolExternallyActivated(toolState.ToolId);
		}
	}

	private void SetToolActive(IdeToolId toolId, bool isActive)
	{
		_toolStateMap[toolId].IsActive = isActive;
	}

	private ToolButton CreateToolButton(IdeToolState toolState)
	{
		var toolButton = Scenes.ToolButton.Instantiate<ToolButton>();

		toolButton.ToolId = toolState.ToolId;

		var icon = IdeToolDescriptors.Descriptors[toolState.ToolId].Icon;

		toolButton.SetButtonIcon(icon);
		toolButton.Toggled += toggledOn =>
		{
			SetToolActive(toolState.ToolId, toggledOn);
			ApplyToolVisibility(toolState.ToolId);
		};
		toolButton.ButtonGroup = _anchorStateMap[toolState.Anchor].ButtonGroup;
		toolButton.ButtonPressed = toolState.IsActive;

		return toolButton;
	}

	private void ApplyToolVisibility(IdeToolId toolId)
	{
		var toolState = _toolStateMap[toolId];
		var toolArea = _anchorStateMap[toolState.Anchor].ToolArea;
		var toolControl = _toolManager.GetControl<Control>(toolId);

		if (toolState.IsActive)
		{
			toolArea.ShowTool(toolControl);
		}
		else
		{
			toolArea.HideTool();
		}

		ApplyBottomAreaVisibility();
	}

	private void ApplySidebarVisibility()
	{
		_leftSidebar.Visible = _toolStateMap.Values.Any(toolState => toolState.Anchor.IsLeft());
		_rightSidebar.Visible = _toolStateMap.Values.Any(toolState => toolState.Anchor.IsRight());
	}

	private void ApplyBottomAreaVisibility()
	{
		_bottomArea.Visible = _toolStateMap.Values.Any(toolState => toolState.Anchor.IsBottom() && toolState.IsActive);
	}

	private sealed record AnchorState(Control SidebarTools, ToolArea ToolArea, ButtonGroup ButtonGroup);
}
