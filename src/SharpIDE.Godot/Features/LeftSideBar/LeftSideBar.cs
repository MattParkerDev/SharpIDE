using Godot;
using SharpIDE.Godot.Features.BottomPanel;

namespace SharpIDE.Godot.Features.LeftSideBar;

public partial class LeftSideBar : Panel
{
	private Button _slnExplorerButton = null!;
	// These are in a ButtonGroup, which handles mutual exclusivity of being toggled on
	private Button _problemsButton = null!;
	private Button _runButton = null!;
	private Button _buildButton = null!;
	private Button _debugButton = null!;
	private Button _ideDiagnosticsButton = null!;
	private Button _nugetButton = null!;
	private Button _testExplorerButton = null!;
	
	public override void _Ready()
	{
		_slnExplorerButton = GetNode<Button>("%SlnExplorerButton");
		_problemsButton = GetNode<Button>("%ProblemsButton");
		_runButton = GetNode<Button>("%RunButton");
		_buildButton = GetNode<Button>("%BuildButton");
		_debugButton = GetNode<Button>("%DebugButton");
		_ideDiagnosticsButton = GetNode<Button>("%IdeDiagnosticsButton");
		_nugetButton = GetNode<Button>("%NugetButton");
		_testExplorerButton = GetNode<Button>("%TestExplorerButton");
		
		_problemsButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.Problems : null);
		_runButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.Run : null);
		_buildButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.Build : null);
		_debugButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.Debug : null);
		_ideDiagnosticsButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.IdeDiagnostics : null);
		_nugetButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.Nuget : null);
		_testExplorerButton.Toggled += toggledOn => GodotGlobalEvents.Instance.BottomPanelTabSelected.InvokeParallelFireAndForget(toggledOn ? BottomPanelType.TestExplorer : null);
		GodotGlobalEvents.Instance.BottomPanelTabExternallySelected.Subscribe(OnBottomPanelTabExternallySelected);
	}

	private async Task OnBottomPanelTabExternallySelected(BottomPanelType arg)
	{
		await this.InvokeAsync(() =>
		{
			switch (arg)
			{
				case BottomPanelType.Run: _runButton.ButtonPressed = true; break;
				case BottomPanelType.Debug: _debugButton.ButtonPressed = true; break;
				case BottomPanelType.Build: _buildButton.ButtonPressed = true; break;
				case BottomPanelType.Problems: _problemsButton.ButtonPressed = true; break;
				case BottomPanelType.IdeDiagnostics: _ideDiagnosticsButton.ButtonPressed = true; break;
				case BottomPanelType.Nuget: _nugetButton.ButtonPressed = true; break;
				case BottomPanelType.TestExplorer: _testExplorerButton.ButtonPressed = true; break;
				default: throw new ArgumentOutOfRangeException(nameof(arg), arg, null);
			}
		});
	}
}
