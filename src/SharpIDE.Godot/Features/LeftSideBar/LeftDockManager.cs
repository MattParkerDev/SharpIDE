using Godot;
using SharpIDE.Godot.Features.Git;
using SharpIDE.Godot.Features.SolutionExplorer;

namespace SharpIDE.Godot.Features.LeftSideBar;

public partial class LeftDockManager : PanelContainer
{
    private SolutionExplorerPanel _solutionExplorerPanel = null!;
    private CommitPanel _commitPanel = null!;

    private Dictionary<LeftDockType, Control> _dockMap = [];

    public SolutionExplorerPanel SolutionExplorerPanel => _solutionExplorerPanel;
    public CommitPanel CommitPanel => _commitPanel;

    public override void _Ready()
    {
        _solutionExplorerPanel = GetNode<SolutionExplorerPanel>("%SolutionExplorerPanel");
        _commitPanel = GetNode<CommitPanel>("%CommitPanel");

        _dockMap = new Dictionary<LeftDockType, Control>
        {
            { LeftDockType.SolutionExplorer, _solutionExplorerPanel },
            { LeftDockType.Commit, _commitPanel }
        };

        GodotGlobalEvents.Instance.LeftDockSelected.Subscribe(OnLeftDockSelected);
        _ = OnLeftDockSelected(LeftDockType.SolutionExplorer);
    }

    public override void _ExitTree()
    {
        GodotGlobalEvents.Instance.LeftDockSelected.Unsubscribe(OnLeftDockSelected);
    }

    private async Task OnLeftDockSelected(LeftDockType dockType)
    {
        await this.InvokeAsync(() =>
        {
            foreach (var kvp in _dockMap)
            {
                kvp.Value.Visible = kvp.Key == dockType;
            }
        });
    }
}
