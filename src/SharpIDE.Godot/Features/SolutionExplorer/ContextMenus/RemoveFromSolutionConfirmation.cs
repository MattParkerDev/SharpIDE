using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel
{
    /// <param name="disabledCheckBoxReason">When not null, the delete from file system checkbox is disabled, with this as the tooltip</param>
    /// <param name="onConfirmed">Called with whether the delete from file system checkbox was checked</param>
    private void OpenRemoveFromSolutionConfirmation(string title, string dialogText, string checkBoxText, string? disabledCheckBoxReason, Func<bool, Task> onConfirmed)
    {
        var confirmedTcs = new TaskCompletionSource<(bool Confirmed, bool DeleteFromFileSystem)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationDialog = new ConfirmationDialog();
        confirmationDialog.Title = title;
        var vBoxContainer = new VBoxContainer();
        vBoxContainer.AddChild(new Label { Text = dialogText });
        var deleteFromFileSystemCheckBox = new CheckBox { Text = checkBoxText };
        if (disabledCheckBoxReason is not null)
        {
            deleteFromFileSystemCheckBox.Disabled = true;
            deleteFromFileSystemCheckBox.TooltipText = disabledCheckBoxReason;
        }
        vBoxContainer.AddChild(deleteFromFileSystemCheckBox);
        confirmationDialog.AddChild(vBoxContainer);
        confirmationDialog.Confirmed += () =>
        {
            confirmedTcs.SetResult((true, deleteFromFileSystemCheckBox.ButtonPressed));
        };
        confirmationDialog.Canceled += () =>
        {
            confirmedTcs.SetResult((false, false));
        };
        AddChild(confirmationDialog);
        confirmationDialog.PopupCentered();
        _ = Task.GodotRun(async () =>
        {
            var (confirmed, deleteFromFileSystem) = await confirmedTcs.Task;
            if (confirmed is false) return;
            await onConfirmed(deleteFromFileSystem && disabledCheckBoxReason is null);
        });
    }

    // Guard against deleting the whole repository, e.g. when the project sits next to the sln file
    private static bool ProjectFolderContainsSolution(SharpIdeProjectModel project)
    {
        IExpandableSharpIdeNode node = project;
        while (node is IChildSharpIdeNode childNode) node = childNode.Parent;
        var solutionDirectory = ((SharpIdeSolutionModel)node).DirectoryPath;
        var relativePath = Path.GetRelativePath(project.DirectoryPath, solutionDirectory);
        return relativePath == "." || (!relativePath.StartsWith("..") && !Path.IsPathRooted(relativePath));
    }
}
