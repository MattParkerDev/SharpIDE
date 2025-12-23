using Godot;

using SharpIDE.Application.Features.Events;

namespace SharpIDE.Godot.Features.Layout;

public partial class IdeMainLayout : Control
{
	private readonly Dictionary<ToolAnchor, ToolArea> _toolAreaMap = [];
	private readonly Dictionary<ToolAnchor, Control> _sidebarToolsMap = [];
	private readonly Dictionary<ToolAnchor, ButtonGroup> _toolButtonGroupMap = [];

	private Dictionary<IdeTool, IdeToolInfo> _toolMap = [];
	private Dictionary<IdeTool, ToolButton> _toolButtonMap = [];

	private Sidebar _leftSidebar = null!;
	private Sidebar _rightSidebar = null!;
	private Control _bottomArea = null!;

	public override void _Ready()
	{
		_leftSidebar = GetNode<Sidebar>("%LeftSidebar");
		_rightSidebar = GetNode<Sidebar>("%RightSidebar");
		_bottomArea = GetNode<Control>("%BottomArea");
		
		_toolAreaMap[ToolAnchor.LeftTop] = GetNode<ToolArea>("%LeftTopToolArea");
		_toolAreaMap[ToolAnchor.RightTop] = GetNode<ToolArea>("%RightTopToolArea");
		_toolAreaMap[ToolAnchor.BottomLeft] = GetNode<ToolArea>("%BottomLeftToolArea");
		_toolAreaMap[ToolAnchor.BottomRight] = GetNode<ToolArea>("%BottomRightToolArea");

		_sidebarToolsMap[ToolAnchor.LeftTop] = _leftSidebar.TopTools;
		_sidebarToolsMap[ToolAnchor.RightTop] = _rightSidebar.TopTools;
		_sidebarToolsMap[ToolAnchor.BottomLeft] = _leftSidebar.BottomTools;
		_sidebarToolsMap[ToolAnchor.BottomRight] = _rightSidebar.BottomTools;

		_toolButtonGroupMap[ToolAnchor.LeftTop] = new ButtonGroup { AllowUnpress = true };
		_toolButtonGroupMap[ToolAnchor.RightTop] = new ButtonGroup { AllowUnpress = true };
		_toolButtonGroupMap[ToolAnchor.BottomLeft] = new ButtonGroup { AllowUnpress = true };
		_toolButtonGroupMap[ToolAnchor.BottomRight] = new ButtonGroup { AllowUnpress = true };
		
		GodotGlobalEvents.Instance.IdeToolExternallySelected.Subscribe(tool =>
		{
			CallDeferred(
				nameof(OnIdeToolExternallySelected),
				Variant.From(tool));
			return Task.CompletedTask;
		});
	}

	private void OnIdeToolExternallySelected(IdeTool tool)
	{
		var anchor = _toolMap[tool].Anchor;
		
		foreach (var toolInfo in _toolMap.Values.Where(t => t.Anchor == anchor))
		{
			_toolButtonMap[toolInfo.Tool].SetPressedNoSignal(toolInfo.Tool == tool);
			toolInfo.IsVisible = toolInfo.Tool == tool;
		}
		
		ToggleTool(tool, toggledOn: true);
	}

	/// <inheritdoc />
	public override Variant _GetDragData(Vector2 atPosition)
	{
		return default;
	}

	/// <inheritdoc />
	public override bool _CanDropData(Vector2 atPosition, Variant data)
	{
		return false;
	}

	/// <inheritdoc />
	public override void _DropData(Vector2 atPosition, Variant data)
	{
		
	}

	public void InitializeLayout(IReadOnlyList<IdeToolInfo> toolInfos)
	{
		_toolMap = toolInfos.ToDictionary(toolInfo => toolInfo.Tool);
		
		foreach (var sidebarToolGroup in _sidebarToolsMap.Values)
		{
			sidebarToolGroup.RemoveChildren();
		}
		
		foreach (var toolInfo in toolInfos)
		{
			var sidebarToolGroup = _sidebarToolsMap[toolInfo.Anchor];

			var button = CreateToolButton(toolInfo);
			_toolButtonMap[toolInfo.Tool] = button;
			
			sidebarToolGroup.AddChild(button);

			if (toolInfo is { IsPinned: true, IsVisible: true })
			{
				_toolAreaMap[toolInfo.Anchor].SetActiveTool(toolInfo);
			}
		}
		
		UpdateSidebarVisibility();
		UpdateBottomAreaVisibility();
	}

	private void UpdateSidebarVisibility()
	{
		_leftSidebar.Visible =
			_toolMap.Values.Any(toolInfo => toolInfo.Anchor.IsLeft() && toolInfo is
			{
				IsPinned: true, IsVisible: true
			});

		_rightSidebar.Visible =
			_toolMap.Values.Any(toolInfo => toolInfo.Anchor.IsLeft() && toolInfo is
			{
				IsPinned: true, IsVisible: true
			});
	}

	private ToolButton CreateToolButton(IdeToolInfo toolInfo)
	{
		var toolButton = ResourceLoader.Load<PackedScene>("uid://gcpcsulb43in").Instantiate<ToolButton>();
		
		toolButton.SetButtonIcon(toolInfo.Icon);
		toolButton.Toggled += toggledOn => ToggleTool(toolInfo.Tool,  toggledOn);
		toolButton.ButtonGroup = _toolButtonGroupMap[toolInfo.Anchor];
		toolButton.ButtonPressed = toolInfo is { IsPinned: true, IsVisible: true };

		return toolButton;
	}

	private void ToggleTool(IdeTool tool, bool toggledOn)
	{
		var toolInfo = _toolMap[tool];
		var toolArea = _toolAreaMap[toolInfo.Anchor];
		
		toolArea.SetActiveTool(toggledOn ? toolInfo : null);
		
		UpdateBottomAreaVisibility();
	}

	private void UpdateBottomAreaVisibility()
	{
		_bottomArea.Visible =
			_toolMap.Values.Any(toolInfo => toolInfo.Anchor.IsBottom()
											 && toolInfo is { IsPinned: true, IsVisible: true });
	}
}
