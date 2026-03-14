using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum FileContextMenuOptions
{
    Open = 0,
    RevealInFileExplorer = 1,
    CopyFullPath = 2,
    Rename = 3,
    Delete = 4
}

public partial class SolutionExplorerPanel
{
    private readonly PackedScene _renameFileDialogScene = GD.Load<PackedScene>("uid://b775b5j4rkxxw");
    private void OpenContextMenuFile(SharpIdeFile file)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("Open", (int)FileContextMenuOptions.Open);
        menu.AddItem("Reveal in File Explorer", (int)FileContextMenuOptions.RevealInFileExplorer);
        menu.AddSeparator();
        menu.AddItem("Copy Full Path", (int)FileContextMenuOptions.CopyFullPath);
        menu.AddSeparator();
        menu.AddItem("Rename", (int)FileContextMenuOptions.Rename);
        menu.AddItem("Delete", (int)FileContextMenuOptions.Delete);
        if (file.Parent is SharpIdeSolutionFolder) menu.SetItemDisabled((int)FileContextMenuOptions.Delete, true);
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
                var renameFileDialog = _renameFileDialogScene.Instantiate<RenameFileDialog>();
                renameFileDialog.File = file;
                AddChild(renameFileDialog);
                renameFileDialog.PopupCentered();
            }
            else if (actionId is FileContextMenuOptions.Delete)
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
        };
			
        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }
}