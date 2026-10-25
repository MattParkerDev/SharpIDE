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
    /// <param name="solutionItemFile">Set when the file is a solution item, i.e. in a solution folder</param>
    private void OpenContextMenuFile(SharpIdeFile file, SharpIdeSolutionItemFile? solutionItemFile = null)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("Open", (int)FileContextMenuOptions.Open);
        menu.AddItem("Reveal in File Explorer", (int)FileContextMenuOptions.RevealInFileExplorer);
        menu.AddSeparator();
        menu.AddItem("Copy Full Path", (int)FileContextMenuOptions.CopyFullPath);
        menu.AddSeparator();
        menu.AddItem("Rename", (int)FileContextMenuOptions.Rename);
        if (solutionItemFile is null) menu.AddItem("Delete", (int)FileContextMenuOptions.Delete);
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
	            OpenRenameFilePopup(file, solutionItemFile);
            }
            else if (actionId is FileContextMenuOptions.Delete)
            {
	            OpenDeleteFileConfirmation(file);
            }
            else if (actionId is FileContextMenuOptions.RemoveFromSolution)
            {
	            OpenRemoveSolutionItemFileConfirmation(file, solutionItemFile!);
            }
        };

        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }

    private void OpenRenameFilePopup(SharpIdeFile file, SharpIdeSolutionItemFile? solutionItemFile)
    {
	    var renameFileDialog = _renameFileDialogScene.Instantiate<RenameFileDialog>();
	    renameFileDialog.File = file;
	    renameFileDialog.SolutionItemFile = solutionItemFile;
	    AddChild(renameFileDialog);
	    renameFileDialog.PopupCentered();
    }

    private void OpenRemoveSolutionItemFileConfirmation(SharpIdeFile file, SharpIdeSolutionItemFile solutionItemFile)
    {
	    OpenRemoveFromSolutionConfirmation(
		    "Remove Solution Item",
		    $"Remove '{solutionItemFile.Name}' from solution?",
		    "Also delete file from the file system",
		    null,
		    async deleteFromFileSystem =>
		    {
			    await _sharpIdeSolutionService.RemoveSolutionItemFile(solutionItemFile);
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
