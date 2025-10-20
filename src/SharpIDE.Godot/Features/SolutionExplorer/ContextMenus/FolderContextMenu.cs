using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum FolderContextMenuOptions
{
    CreateNew = 1,
    RevealInFileExplorer = 2,
    Delete = 3
}

file enum CreateNewSubmenuOptions
{
    Directory = 1,
    CSharpFile = 2
}

public partial class SolutionExplorerPanel
{
    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;
    private void OpenContextMenuFolder(SharpIdeFolder folder)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        
        var createNewSubmenu = new PopupMenu();
        menu.AddSubmenuNodeItem("Add", createNewSubmenu, (int)FolderContextMenuOptions.CreateNew);
        createNewSubmenu.AddItem("Directory", (int)CreateNewSubmenuOptions.Directory);
        createNewSubmenu.AddItem("C# File", (int)CreateNewSubmenuOptions.CSharpFile);
        createNewSubmenu.IdPressed += id => OnCreateNewSubmenuPressed(id, folder);
        
        menu.AddItem("Reveal in File Explorer", (int)FolderContextMenuOptions.RevealInFileExplorer);
        menu.AddItem("Delete", (int)FolderContextMenuOptions.Delete);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id =>
        {
            var actionId = (FolderContextMenuOptions)id;
            if (actionId is FolderContextMenuOptions.RevealInFileExplorer)
            {
                OS.ShellOpen(folder.Path);
            }
            else if (actionId is FolderContextMenuOptions.Delete)
            {
                var confirmedTcs = new TaskCompletionSource<bool>();
                var confirmationDialog = new ConfirmationDialog();
                confirmationDialog.Title = "Delete";
                confirmationDialog.DialogText = $"Delete '{folder.Name}' file?";
                confirmationDialog.Confirmed += () =>
                {
                    confirmedTcs.SetResult(true);
                };
                confirmationDialog.Canceled += () =>
                {
                    confirmedTcs.SetResult(false);
                };
                AddChild(confirmationDialog);
                confirmationDialog.PopupCentered();
                _ = Task.GodotRun(async () =>
                {
                    var confirmed = await confirmedTcs.Task;
                    if (confirmed)
                    {
                        await _ideFileOperationsService.DeleteDirectory(folder);
                    }
                });
            }
        };
			
        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }

    private readonly PackedScene _newDirectoryDialogScene = GD.Load<PackedScene>("uid://bgi4u18y8pt4x");
    private readonly PackedScene _newCsharpFileDialogScene = GD.Load<PackedScene>("uid://chnb7gmcdg0ww");
    private void OnCreateNewSubmenuPressed(long id, IFolderOrProject folder)
    {
        var actionId = (CreateNewSubmenuOptions)id;
        if (actionId is CreateNewSubmenuOptions.Directory)
        {
            var newDirectoryDialog = _newDirectoryDialogScene.Instantiate<NewDirectoryDialog>();
            newDirectoryDialog.ParentFolder = folder;
            AddChild(newDirectoryDialog);
            newDirectoryDialog.PopupCentered();
        }
        else if (actionId is CreateNewSubmenuOptions.CSharpFile)
        {
            var newCsharpFileDialog = _newCsharpFileDialogScene.Instantiate<NewCsharpFileDialog>();
            newCsharpFileDialog.ParentNode = folder;
            AddChild(newCsharpFileDialog);
            newCsharpFileDialog.PopupCentered();
        }
    }
}