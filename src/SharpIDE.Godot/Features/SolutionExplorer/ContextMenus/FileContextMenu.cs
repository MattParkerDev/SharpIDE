using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum FileContextMenuOptions
{
    Open = 0,
    RevealInFileExplorer = 1,
    CopyFullPath = 2,
    Rename = 3,
    Delete = 4,
    RemoveFromSolution = 5
}

public partial class SolutionExplorerPanel
{
    private readonly PackedScene _renameFileDialogScene = GD.Load<PackedScene>("uid://b775b5j4rkxxw");
    /// <param name="solutionFile">Set when the file is a solution item, i.e. in a solution folder</param>
    private void OpenContextMenuFile(SharpIdeFile file, SharpIdeSolutionFile? solutionFile = null)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("Open", (int)FileContextMenuOptions.Open);
        menu.AddItem("Reveal in File Explorer", (int)FileContextMenuOptions.RevealInFileExplorer);
        menu.AddSeparator();
        menu.AddItem("Copy Full Path", (int)FileContextMenuOptions.CopyFullPath);
        menu.AddSeparator();
        menu.AddItem("Rename", (int)FileContextMenuOptions.Rename);
        if (solutionFile is null) menu.AddItem("Delete", (int)FileContextMenuOptions.Delete);
        else menu.AddItem("Remove from Solution", (int)FileContextMenuOptions.RemoveFromSolution);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id =>
        {
            var actionId = (FileContextMenuOptions)id;
            if (actionId is FileContextMenuOptions.Open)
            {
                GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(file, null);
            }
            else if (actionId is FileContextMenuOptions.RevealInFileExplorer)
            {
                OS.ShellShowInFileManager(file.Path);
            }
            else if (actionId is FileContextMenuOptions.CopyFullPath)
            {
                DisplayServer.ClipboardSet(file.Path);
            }
            else if (actionId is FileContextMenuOptions.Rename)
            {
	            OpenRenameFilePopup(file, solutionFile);
            }
            else if (actionId is FileContextMenuOptions.Delete)
            {
	            OpenDeleteFileConfirmation(file);
            }
            else if (actionId is FileContextMenuOptions.RemoveFromSolution)
            {
	            OpenRemoveSolutionFileConfirmation(file, solutionFile!);
            }
        };

        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }

    private void OpenRenameFilePopup(SharpIdeFile file, SharpIdeSolutionFile? solutionFile)
    {
	    var renameFileDialog = _renameFileDialogScene.Instantiate<RenameFileDialog>();
	    renameFileDialog.File = file;
	    renameFileDialog.SolutionFile = solutionFile;
	    AddChild(renameFileDialog);
	    renameFileDialog.PopupCentered();
    }

    private void OpenRemoveSolutionFileConfirmation(SharpIdeFile file, SharpIdeSolutionFile solutionFile)
    {
	    OpenRemoveFromSolutionConfirmation(
		    "Remove Solution Item",
		    $"Remove '{solutionFile.Name}' from solution?",
		    "Also delete file from the file system",
		    null,
		    async deleteFromFileSystem =>
		    {
			    await _sharpIdeSolutionService.RemoveSolutionFile(solutionFile);
			    if (deleteFromFileSystem) await _ideFileOperationsService.DeleteFile(file);
		    });
    }

    private void OpenDeleteFileConfirmation(SharpIdeFile file)
    {
	    var confirmedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
	    var confirmationDialog = new ConfirmationDialog();
	    confirmationDialog.Title = "Delete";
	    confirmationDialog.DialogText = $"Delete '{file.Name.Value}' file?";
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
			    await _ideFileOperationsService.DeleteFile(file);
		    }
	    });
    }
}
