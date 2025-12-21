using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Build;
using SharpIDE.Godot.Features.Debug_;
using SharpIDE.Godot.Features.IdeDiagnostics;
using SharpIDE.Godot.Features.Nuget;
using SharpIDE.Godot.Features.Problems;
using SharpIDE.Godot.Features.Run;
using SharpIDE.Godot.Features.TestExplorer;

namespace SharpIDE.Godot.Features.BottomPanel;

public partial class MultiFunctionPanel : Panel
{
	private RunPanel _runPanel = null!;
	private DebugPanel _debugPanel = null!;
	private BuildPanel _buildPanel = null!;
	private ProblemsPanel _problemsPanel = null!;
	private IdeDiagnosticsPanel _ideDiagnosticsPanel = null!;
	private NugetPanel _nugetPanel = null!;
	private TestExplorerPanel _testExplorerPanel = null!;

	private Dictionary<BottomPanelType, Control> _panelTypeMap = [];
	
	public override void _Ready()
	{
		_runPanel = GetNode<RunPanel>("%RunPanel");
		_debugPanel = GetNode<DebugPanel>("%DebugPanel");
		_buildPanel = GetNode<BuildPanel>("%BuildPanel");
		_problemsPanel = GetNode<ProblemsPanel>("%ProblemsPanel");
		_ideDiagnosticsPanel = GetNode<IdeDiagnosticsPanel>("%IdeDiagnosticsPanel");
		_nugetPanel = GetNode<NugetPanel>("%NugetPanel");
		_testExplorerPanel = GetNode<TestExplorerPanel>("%TestExplorerPanel");
		
		_panelTypeMap = new Dictionary<BottomPanelType, Control>
		{
			{ BottomPanelType.Run, _runPanel },
			{ BottomPanelType.Debug, _debugPanel },
			{ BottomPanelType.Build, _buildPanel },
			{ BottomPanelType.Problems, _problemsPanel },
			{ BottomPanelType.IdeDiagnostics, _ideDiagnosticsPanel },
			{ BottomPanelType.Nuget, _nugetPanel },
			{ BottomPanelType.TestExplorer, _testExplorerPanel }
		};
		
		_ = OnBottomPanelTabSelected(BottomPanelType.Run);

		GodotGlobalEvents.Instance.BottomPanelTabSelected.Subscribe(OnBottomPanelTabSelected);
	}

	public override void _ExitTree()
	{
		GodotGlobalEvents.Instance.BottomPanelTabSelected.Subscribe(OnBottomPanelTabSelected);
	}

	private async Task OnBottomPanelTabSelected(BottomPanelType? type)
	{
		await this.InvokeAsync(() =>
		{
			Visible = type is not null;
			
			foreach (var kvp in _panelTypeMap)
			{
				kvp.Value.Visible = kvp.Key == type;
			}
		});
	}
}
