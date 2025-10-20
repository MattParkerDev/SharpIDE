using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

public partial class NewDirectoryDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;
    
    public IFolderOrProject ParentFolder { get; set; } = null!;

    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;

    public override void _Ready()
    {
        _nameLineEdit = GetNode<LineEdit>("%DirectoryNameLineEdit");
        _nameLineEdit.GrabFocus();
        _nameLineEdit.SelectAll();
        Confirmed += OnConfirmed;
    }

    private void OnConfirmed()
    {
        var directoryName = _nameLineEdit.Text.Trim();
        if (string.IsNullOrEmpty(directoryName))
        {
            GD.PrintErr("Directory name cannot be empty.");
            return;
        }

        _ = Task.GodotRun(async () =>
        {
           await _ideFileOperationsService.CreateDirectory(ParentFolder, directoryName);
        });
    }
}