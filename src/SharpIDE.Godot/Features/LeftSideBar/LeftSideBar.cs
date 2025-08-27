using Godot;

namespace SharpIDE.Godot.Features.LeftSideBar;

public partial class LeftSideBar : Panel
{
    private Button _slnExplorerButton = null!;
    private Button _problemsButton = null!;
    private Button _runButton = null!;
    private Button _buildButton = null!;
    
    public override void _Ready()
    {
        _slnExplorerButton = GetNode<Button>("%SlnExplorerButton");
        _problemsButton = GetNode<Button>("%ProblemsButton");
        _runButton = GetNode<Button>("%RunButton");
        _buildButton = GetNode<Button>("%BuildButton");
        
        _problemsButton.Pressed += () => GodotGlobalEvents.InvokeLeftSideBarButtonClicked(BottomPanelType.Problems);
        _runButton.Pressed += () => GodotGlobalEvents.InvokeLeftSideBarButtonClicked(BottomPanelType.Run);
        _buildButton.Pressed += () => GodotGlobalEvents.InvokeLeftSideBarButtonClicked(BottomPanelType.Build);
    }
}