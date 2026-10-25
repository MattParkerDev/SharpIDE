using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum SolutionContextMenuOptions
{
    Add = 1,
    ReloadSolution
}

file enum SolutionAddSubmenuOptions
{
    SolutionFolder = 1,
    Project
}

public partial class SolutionExplorerPanel
{
    [Inject] private readonly SharpIdeSolutionService _sharpIdeSolutionService = null!;
    
    private void OpenContextMenuSolution(SharpIdeSolutionModel solution, TreeItem solutionTreeItem)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        
        var createNewSubmenu = new PopupMenu();
        menu.AddSubmenuNodeItem("Add", createNewSubmenu, (int)SolutionContextMenuOptions.Add);
        createNewSubmenu.AddItem("New Project", (int)SolutionAddSubmenuOptions.Project);
        createNewSubmenu.AddItem("New Solution Folder", (int)SolutionAddSubmenuOptions.SolutionFolder);
        createNewSubmenu.IdPressed += id => OnSolutionAddSubmenuPressed(id, solution);
        
        menu.AddItem("Reload Solution", (int)SolutionContextMenuOptions.ReloadSolution);
        
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id =>
        {
            var actionId = (SolutionContextMenuOptions)id;
            if (actionId == SolutionContextMenuOptions.ReloadSolution)
            {
                _ = Task.GodotRun(async () =>
                {
                    await _sharpIdeSolutionService.ReloadSolution();
                });
            }
        };
			
        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }

    private void OnSolutionAddSubmenuPressed(long id, SharpIdeSolutionModel solution)
    {
        var actionId = (SolutionAddSubmenuOptions)id;
        if (actionId is SolutionAddSubmenuOptions.Project)
        {
            OpenNewProjectWindow(solution);
        }
        else if (actionId is SolutionAddSubmenuOptions.SolutionFolder)
        {
            OpenNewSolutionFolderDialog(solution);
        }
    }
}