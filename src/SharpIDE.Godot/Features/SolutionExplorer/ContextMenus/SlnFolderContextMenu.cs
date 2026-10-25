using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.NewProject;
using SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum SlnFolderContextMenuOptions
{
    Add = 1,
    Remove
}

file enum SlnFolderAddSubmenuOptions
{
    Project = 1,
    SolutionFolder
}

public partial class SolutionExplorerPanel
{
    private PackedScene _newProjectContainerScene = ResourceLoader.Load<PackedScene>("uid://vkqiqm661f");
    private readonly PackedScene _newSolutionFolderDialogScene = GD.Load<PackedScene>("res://Features/SolutionExplorer/ContextMenus/Dialogs/NewSolutionFolderDialog.tscn");
    
    private void OpenContextMenuSlnFolder(SharpIdeSolutionFolder slnFolder, TreeItem slnFolderTreeItem)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        
        var createNewSubmenu = new PopupMenu();
        menu.AddSubmenuNodeItem("Add", createNewSubmenu, (int)SlnFolderContextMenuOptions.Add);
        createNewSubmenu.AddItem("New Project", (int)SlnFolderAddSubmenuOptions.Project);
        createNewSubmenu.AddItem("New Solution Folder", (int)SlnFolderAddSubmenuOptions.SolutionFolder);
        createNewSubmenu.IdPressed += id => OnSlnFolderAddSubmenuPressed(id, slnFolder);
        menu.AddSeparator();
        menu.AddItem("Remove", (int)SlnFolderContextMenuOptions.Remove);
        
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id =>
        {
            var actionId = (SlnFolderContextMenuOptions)id;
            if (actionId is SlnFolderContextMenuOptions.Remove)
            {
                OpenRemoveSlnFolderConfirmation(slnFolder);
            }
        };
			
        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }

    private void OnSlnFolderAddSubmenuPressed(long id, SharpIdeSolutionFolder slnFolder)
    {
        var actionId = (SlnFolderAddSubmenuOptions)id;
        if (actionId is SlnFolderAddSubmenuOptions.Project)
        {
            OpenNewProjectWindow(slnFolder);
        }
        else if (actionId is SlnFolderAddSubmenuOptions.SolutionFolder)
        {
            OpenNewSolutionFolderDialog(slnFolder);
        }
    }

    private void OpenRemoveSlnFolderConfirmation(SharpIdeSolutionFolder slnFolder)
    {
        List<SharpIdeProjectModel> projects = [];
        List<SharpIdeSolutionFile> solutionFiles = [];
        CollectSlnFolderContents(slnFolder);

        var projectsToDelete = projects.Where(p => p.Folder is not null && ProjectFolderContainsSolution(p) is false).ToList();
        var filesToDelete = solutionFiles.Select(f => f.File).OfType<SharpIdeFile>().ToList();
        var dialogText = projects.Count is 0 && solutionFiles.Count is 0
            ? $"Remove solution folder '{slnFolder.Name}' from solution?"
            : $"Remove solution folder '{slnFolder.Name}' from solution?\nThis also removes the {projects.Count} project(s) and {solutionFiles.Count} solution item(s) it contains.";
        string? disabledCheckBoxReason = (projectsToDelete.Count, filesToDelete.Count) switch
        {
            (0, 0) when projects.Count is not 0 => "The contained project folders contain the solution, so they cannot be deleted",
            (0, 0) => "The solution folder has no projects or solution items to delete",
            _ => null
        };

        OpenRemoveFromSolutionConfirmation(
            "Remove Solution Folder",
            dialogText,
            "Also delete contained project folders and solution item files from the file system",
            disabledCheckBoxReason,
            async deleteFromFileSystem =>
            {
                await _sharpIdeSolutionService.RemoveSolutionFolder(slnFolder);
                if (deleteFromFileSystem is false) return;
                foreach (var file in filesToDelete.Where(f => File.Exists(f.Path)))
                {
                    await _ideFileOperationsService.DeleteFile(file);
                }
                // A project folder may have already been deleted, e.g. if it was nested inside another project's folder
                foreach (var project in projectsToDelete.Where(p => Directory.Exists(p.DirectoryPath)))
                {
                    await _ideFileOperationsService.DeleteDirectory(project.Folder!);
                }
            });
        return;

        void CollectSlnFolderContents(SharpIdeSolutionFolder folder)
        {
            projects.AddRange(folder.Projects);
            solutionFiles.AddRange(folder.Files);
            foreach (var subFolder in folder.Folders) CollectSlnFolderContents(subFolder);
        }
    }

    private void OpenNewSolutionFolderDialog(ISolutionOrSolutionFolder parentNode)
    {
        var newSolutionFolderDialog = _newSolutionFolderDialogScene.Instantiate<NewSolutionFolderDialog>();
        newSolutionFolderDialog.ParentNode = parentNode;
        AddChild(newSolutionFolderDialog);
        newSolutionFolderDialog.PopupCentered();
    }

    private void OpenNewProjectWindow(ISolutionOrSolutionFolder parentNode)
    {
        var newProjectContainer = _newProjectContainerScene.Instantiate<NewProjectContainer>();
        newProjectContainer.ParentNode = parentNode;
        var popupWindow = new Window
        {
            Title = "New Project",
            InitialPosition = Window.WindowInitialPosition.CenterMainWindowScreen,
            Transient = true,
            PopupWindow = true,
            PopupWMHint = true
        };
        popupWindow.CloseRequested += () =>
        {
            if (newProjectContainer.IsCreating) return;
            this.RemoveChildAndQueueFree(popupWindow);
        };
        popupWindow.AddChild(newProjectContainer);
        AddChild(popupWindow);
        popupWindow.PopupCenteredRatio(0.5f);
    }
}
