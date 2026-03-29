using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.LanguageExtensions;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum FolderContextMenuOptions
{
    CreateNew = 1,
    RevealInFileExplorer = 2,
    Delete = 3,
    Rename = 4
}

file enum CreateNewSubmenuOptions
{
    Directory = 1,
    CSharpFile = 2
}

public partial class SolutionExplorerPanel
{
    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;
    [Inject] private readonly LanguageExtensionRegistry _languageExtensionRegistry = null!;
    
    private readonly PackedScene _newDirectoryDialogScene = GD.Load<PackedScene>("uid://bgi4u18y8pt4x");
    private readonly PackedScene _newCsharpFileDialogScene = GD.Load<PackedScene>("uid://chnb7gmcdg0ww");
    private readonly PackedScene _renameDirectoryDialogScene = GD.Load<PackedScene>("uid://btebkg8bo3b37");
    private void OpenContextMenuFolder(SharpIdeFolder folder, TreeItem folderTreeItem)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        
        var createNewSubmenu = new PopupMenu();
        menu.AddSubmenuNodeItem("Add", createNewSubmenu, (int)FolderContextMenuOptions.CreateNew);
        createNewSubmenu.AddItem("Directory", (int)CreateNewSubmenuOptions.Directory);
        createNewSubmenu.AddItem("C# File", (int)CreateNewSubmenuOptions.CSharpFile);

        var extensionItems = BuildExtensionMenuItems(createNewSubmenu);
        createNewSubmenu.IdPressed += id =>
        {
            if (extensionItems.TryGetValue((int)id, out var ext))
                ShowNewExtensionFileDialog(folder, ext);
            else
                OnCreateNewSubmenuPressed(id, folder);
        };
        
        menu.AddItem("Reveal in File Explorer", (int)FolderContextMenuOptions.RevealInFileExplorer);
        menu.AddItem("Delete", (int)FolderContextMenuOptions.Delete);
        menu.AddItem("Rename", (int)FolderContextMenuOptions.Rename);
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
                var confirmedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var confirmationDialog = new ConfirmationDialog();
                confirmationDialog.Title = "Delete";
                confirmationDialog.DialogText = $"Delete '{folder.Name.Value}' file?";
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
            else if (actionId is FolderContextMenuOptions.Rename)
            {
                var renameDirectoryDialog = _renameDirectoryDialogScene.Instantiate<RenameDirectoryDialog>();
                renameDirectoryDialog.Folder = folder;
                AddChild(renameDirectoryDialog);
                renameDirectoryDialog.PopupCentered();
            }
        };
			
        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }

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

    // Returns a dict of (menuItemId → fileExtension) for all installed language extension types.
    // Items are added directly to the provided submenu starting at id 100.
    private Dictionary<int, string> BuildExtensionMenuItems(PopupMenu submenu)
    {
        var items = new Dictionary<int, string>();
        var nextId = 100;
        var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in _languageExtensionRegistry.GetAllExtensions())
        {
            foreach (var lang in ext.Languages)
            {
                foreach (var fileExt in lang.FileExtensions)
                {
                    if (!seenExtensions.Add(fileExt)) continue;
                    var label = $"{fileExt.TrimStart('.').ToUpperInvariant()} File";
                    submenu.AddItem(label, nextId);
                    items[nextId] = fileExt;
                    nextId++;
                }
            }
        }

        return items;
    }

    private void ShowNewExtensionFileDialog(IFolderOrProject parent, string extension)
    {
        var extUpper = extension.TrimStart('.').ToUpperInvariant();
        var dialog = new ConfirmationDialog
        {
            Title = $"New {extUpper} File",
            MinSize = new Vector2I(340, 0)
        };
        var lineEdit = new LineEdit
        {
            Text = $"Template{extension}",
            CustomMinimumSize = new Vector2(300, 0),
            SelectAllOnFocus = true
        };
        dialog.AddChild(lineEdit);
        dialog.Confirmed += () =>
        {
            var fileName = lineEdit.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileName)) return;
            if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                fileName += extension;
            _ = Task.GodotRun(async () =>
            {
                var file = await _ideFileOperationsService.CreateGenericFile(parent, fileName);
                GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(file, null);
            });
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }
}