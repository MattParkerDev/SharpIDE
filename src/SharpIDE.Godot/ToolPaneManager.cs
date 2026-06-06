using Godot;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.ToolPanes;

namespace SharpIDE.Godot;

public partial class ToolPaneManager : Node
{
	private static readonly ToolPaneResource _buildPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://4m8aaxjeoraa");
	private static readonly ToolPaneResource _debugPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://bqtomxt8uavs2");
	private static readonly ToolPaneResource _nugetPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://pqyljb14it08");
	private static readonly ToolPaneResource _problemsPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://cpm77v6x87l7o");
	private static readonly ToolPaneResource _runPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://bo3vtb7smg7uo");
	private static readonly ToolPaneResource _slnExplorerPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://ch4wy8i05fgf4");
	private static readonly ToolPaneResource _testsPaneResource = ResourceLoader.Load<ToolPaneResource>("uid://bjpvpwr2o2jbc");

	private static readonly PackedScene _toolPaneButtonScene = ResourceLoader.Load<PackedScene>("uid://tag6haeskfi5");


	private readonly Dictionary<ToolPaneLocation, (ToolPaneButtonsContainer, ToolPaneContainer)> _toolPaneContainersAndButtonContainersByLocation = [];

	private readonly Dictionary<ToolPaneLocation, List<ToolPaneResource>> DefaultLayout = new()
	{
		[ToolPaneLocation.TopLeft] = [_slnExplorerPaneResource],
		[ToolPaneLocation.BottomLeft] = [_problemsPaneResource, _runPaneResource, _debugPaneResource, _buildPaneResource, _nugetPaneResource, _testsPaneResource]
	};

	public override void _Ready()
	{
		foreach (var (toolPaneButtonsContainer, toolPaneContainer) in GetTree().GetNodesInGroup("ToolPaneButtonContainers").Cast<ToolPaneButtonsContainer>().OrderBy(s => s.Location).Zip(GetTree().GetNodesInGroup("ToolPaneContainers").Cast<ToolPaneContainer>().OrderBy(s => s.Location)))
		{
			if (toolPaneButtonsContainer.Location != toolPaneContainer.Location) throw new InvalidOperationException("ToolPaneButtonsContainer and ToolPaneContainer locations do not match up");
			_toolPaneContainersAndButtonContainersByLocation.Add(toolPaneButtonsContainer.Location, (toolPaneButtonsContainer, toolPaneContainer));
			var location = toolPaneButtonsContainer.Location;
			if (location is ToolPaneLocation.Unknown) throw new InvalidOperationException("ToolPaneLocation must be set");
			var buttonGroup = toolPaneButtonsContainer.ButtonGroup;
			buttonGroup.Pressed += _ =>
			{
				var pressedButton = (ToolPaneButton?)buttonGroup.GetPressedButton();
				foreach (var resource in DefaultLayout[location]) resource.PaneInstance!.Visible = false;
				if (pressedButton is null)
				{
					toolPaneContainer.Visible = false;
					return;
				}
				var matchedResource = DefaultLayout[location].First(r => r.ToolPaneType == pressedButton.ToolPaneType);
				matchedResource.PaneInstance!.Visible = true;
				toolPaneContainer.Visible = true;
			};
		}
		foreach (var (location, paneResources) in DefaultLayout)
		{
			foreach (var paneResource in paneResources)
			{
				if (paneResource.ToolPaneType == ToolPaneType.Unknown) throw new InvalidOperationException("ToolPaneType must be set");
				var (buttonContainer, paneContainer) = _toolPaneContainersAndButtonContainersByLocation[location];
				var paneInstance = paneResource.Pane.Instantiate<Control>();
				paneInstance.Visible = false;
				paneResource.PaneInstance = paneInstance;
				paneContainer.AddChild(paneInstance);

				var buttonInstance = _toolPaneButtonScene.Instantiate<ToolPaneButton>();
				buttonInstance.ToolPaneType = paneResource.ToolPaneType;
				buttonInstance.Text = paneResource.ButtonLabel;
				buttonInstance.Icon = paneResource.ButtonIcon;
				buttonInstance.ButtonGroup = buttonContainer.ButtonGroup;
				buttonInstance.SetPressedNoSignal(false);
				if (buttonInstance.ToolPaneType is ToolPaneType.SlnExplorer or ToolPaneType.Problems)
				{
					Callable.From(() =>
					{
						buttonInstance.ButtonPressed = true;
					}).CallDeferred();
				}

				buttonContainer.AddChild(buttonInstance);
			}
		}
		GodotGlobalEvents.Instance.ToolPaneExternallyActivated.Subscribe(OnToolPaneExternallyActivated);
		GlobalEvents.Instance.ProjectStartedRunning.Subscribe(OnProjectStartedRunning);
		GlobalEvents.Instance.ProjectStartedDebugging.Subscribe(OnProjectStartedDebugging);
	}

	private async Task OnProjectStartedRunning(SharpIdeProjectModel _) => GodotGlobalEvents.Instance.ToolPaneExternallyActivated.InvokeParallelFireAndForget(ToolPaneType.Run);
	private async Task OnProjectStartedDebugging(SharpIdeProjectModel arg) => GodotGlobalEvents.Instance.ToolPaneExternallyActivated.InvokeParallelFireAndForget(ToolPaneType.Debug);

	private async Task OnToolPaneExternallyActivated(ToolPaneType arg)
	{
		var (location, resource) = DefaultLayout.SelectMany(kvp => kvp.Value.Select(r => (kvp.Key, r))).FirstOrDefault(x => x.r.ToolPaneType == arg);
		if (resource is null) throw new InvalidOperationException($"No pane registered for ToolPaneType {arg}");

		var (buttonContainer, _) = _toolPaneContainersAndButtonContainersByLocation[location];
		await this.InvokeAsync(() =>
		{
			var button = buttonContainer.GetChildren().Cast<ToolPaneButton>().First(b => b.ToolPaneType == arg);
			button.ButtonPressed = true;
		});
	}
}
