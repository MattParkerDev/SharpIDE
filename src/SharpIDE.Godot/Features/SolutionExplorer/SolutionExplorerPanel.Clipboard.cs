using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel
{
    private void CopySelectedNodesToSlnExplorerClipboard() => AddSelectedNodesToSlnExplorerClipboard(ClipboardOperation.Copy);
    private void CutSelectedNodeToSlnExplorerClipboard() => AddSelectedNodesToSlnExplorerClipboard(ClipboardOperation.Cut);
    private void AddSelectedNodesToSlnExplorerClipboard(ClipboardOperation clipboardOperation)
    {
        var selectedItems = GetSelectedTreeItems();
        if (selectedItems.Count is 0) return;
        _itemsOnClipboard = (selectedItems
            .Select(item =>
            {
                var sharpIdeNode = item.SharpIdeNode;
                IFileOrFolder? result = sharpIdeNode switch
                {
                    SharpIdeFile file => file,
                    SharpIdeFolder folder => folder,
                    _ => null
                };
                return result;
            })
            .OfType<IFileOrFolder>()
            .ToList(),
            clipboardOperation);
        GD.Print($"Solution Explorer - Added {_itemsOnClipboard.Value.Item1.Count} items to clipboard with operation {clipboardOperation}");
    }

    private List<TreeItem> GetSelectedTreeItems()
    {
        var selectedItems = new List<TreeItem>();
        var currentItem = _tree.GetNextSelected(null);
        while (currentItem != null)
        {
            selectedItems.Add(currentItem);
            currentItem = _tree.GetNextSelected(currentItem);
        }
        return selectedItems;
    }

    private void ClearSlnExplorerClipboard()
    {
        _itemsOnClipboard = null;
    }

    private void CopyNodesFromClipboardToSelectedNode()
    {
        var selected = _tree.GetSelected();
        if (selected is null || _itemsOnClipboard is null) return;
        var sharpIdeNode = selected.SharpIdeNode;
        SharpIdeFolder? destinationFolder = sharpIdeNode switch
        {
            SharpIdeFolder f => f,
            SharpIdeProjectModel p => p.Folder,
            _ => null
        };
        if (destinationFolder is null) return;

        var (filesToPaste, operation) = _itemsOnClipboard.Value;
        _itemsOnClipboard = null;
        _ = Task.GodotRun(async () =>
        {
            if (operation is ClipboardOperation.Copy)
            {
                foreach (var fileOrFolderToPaste in filesToPaste)
                {
                    if (fileOrFolderToPaste is SharpIdeFolder folderToPaste)
                    {
                        await _ideFileOperationsService.CopyDirectory(destinationFolder, folderToPaste.Path, folderToPaste.Name.Value);
                    }
                    else if (fileOrFolderToPaste is SharpIdeFile fileToPaste)
                    {
                        await _ideFileOperationsService.CopyFile(destinationFolder, fileToPaste.Path, fileToPaste.Name.Value);
                    }
                }
            }
            // This will blow up if cutting a file into a directory that already has a file with the same name, but I don't really want to handle renaming cut-pasted files for MVP
            else if (operation is ClipboardOperation.Cut)
            {
                foreach (var fileOrFolderToPaste in filesToPaste)
                {
                    if (fileOrFolderToPaste is SharpIdeFolder folderToPaste)
                    {
                        await _ideFileOperationsService.MoveDirectory(destinationFolder, folderToPaste);
                    }
                    else if (fileOrFolderToPaste is SharpIdeFile fileToPaste)
                    {
                        await _ideFileOperationsService.MoveFile(destinationFolder, fileToPaste);
                    }
                }
            }
        });
    }
}
