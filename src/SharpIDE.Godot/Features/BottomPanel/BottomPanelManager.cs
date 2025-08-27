using Godot;

namespace SharpIDE.Godot.Features.BottomPanel;

public partial class BottomPanelManager : Panel
{
    private Control _runPanel = null!;
    private Control _buildPanel = null!;
    private Control _problemsPanel = null!;

    private Dictionary<BottomPanelType, Control> _panelTypeMap = [];
    private BottomPanelType? _currentPanelType = BottomPanelType.Run;
    
    public override void _Ready()
    {
        _runPanel = GetNode<Control>("%RunPanel");
        _buildPanel = GetNode<Control>("%BuildPanel");
        _problemsPanel = GetNode<Control>("%ProblemsPanel");
        _panelTypeMap = new Dictionary<BottomPanelType, Control>
        {
            { BottomPanelType.Run, _runPanel },
            { BottomPanelType.Build, _buildPanel },
            { BottomPanelType.Problems, _problemsPanel }
        };

        GodotGlobalEvents.LeftSideBarButtonClicked += OnLeftSideBarButtonClicked;
    }

    private async Task OnLeftSideBarButtonClicked(BottomPanelType type)
    {
        await this.InvokeAsync(() =>
        {
            if (type == _currentPanelType)
            {
                _currentPanelType = null;
                // TODO: Ask parent to to collapse slider.
            }
            foreach (var kvp in _panelTypeMap)
            {
                kvp.Value.Visible = kvp.Key == type;
            }
        });
    }
}