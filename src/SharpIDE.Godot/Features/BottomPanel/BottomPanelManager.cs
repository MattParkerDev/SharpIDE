using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.BottomPanel;

public partial class BottomPanelManager : Panel
{
    public SharpIdeSolutionModel? Solution
    {
        get;
        set
        {
            field = value;
            _problemsPanel.Solution = value;
        }
    }

    private Control _runPanel = null!;
    private Control _debugPanel = null!;
    private Control _buildPanel = null!;
    private ProblemsPanel _problemsPanel = null!;

    private Dictionary<BottomPanelType, Control> _panelTypeMap = [];
    
    public override void _Ready()
    {
        _runPanel = GetNode<Control>("%RunPanel");
        _debugPanel = GetNode<Control>("%DebugPanel");
        _buildPanel = GetNode<Control>("%BuildPanel");
        _problemsPanel = GetNode<ProblemsPanel>("%ProblemsPanel");
        
        _panelTypeMap = new Dictionary<BottomPanelType, Control>
        {
            { BottomPanelType.Run, _runPanel },
            { BottomPanelType.Debug, _debugPanel },
            { BottomPanelType.Build, _buildPanel },
            { BottomPanelType.Problems, _problemsPanel }
        };

        GodotGlobalEvents.BottomPanelTabSelected += OnBottomPanelTabSelected;
    }

    private async Task OnBottomPanelTabSelected(BottomPanelType? type)
    {
        await this.InvokeAsync(() =>
        {
            if (type == null)
            {
                GodotGlobalEvents.InvokeBottomPanelVisibilityChangeRequested(false);
            }
            else
            {
                GodotGlobalEvents.InvokeBottomPanelVisibilityChangeRequested(true);
            }
            foreach (var kvp in _panelTypeMap)
            {
                kvp.Value.Visible = kvp.Key == type;
            }
        });
    }
}