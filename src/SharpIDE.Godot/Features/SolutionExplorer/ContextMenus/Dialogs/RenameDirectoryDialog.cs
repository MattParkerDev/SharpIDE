using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

public partial class RenameDirectoryDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;
    
    public SharpIdeFolder Folder { get; set; } = null!;

    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;
    
    private bool _isNameValid = true;
    private string _folderParentPath = null!;

    public override void _Ready()
    {
        _folderParentPath = Path.GetDirectoryName(Folder.Path)!;
        _nameLineEdit = GetNode<LineEdit>("%DirectoryNameLineEdit");
        _nameLineEdit.Text = Folder.Name;
        _nameLineEdit.GrabFocus();
        _nameLineEdit.SelectAll();
        _nameLineEdit.TextChanged += ValidateNewDirectoryName;
        Confirmed += OnConfirmed;
    }

    private void ValidateNewDirectoryName(string newDirectoryNameText)
    {
        _isNameValid = true;
        var newDirectoryName = newDirectoryNameText.Trim();
        if (string.IsNullOrEmpty(newDirectoryName) || Directory.Exists(Path.Combine(_folderParentPath, newDirectoryName)))
        {
            _isNameValid = false;
        }
        var textColour = _isNameValid ? new Color(1, 1, 1) : new Color(1, 0, 0);
        _nameLineEdit.AddThemeColorOverride(ThemeStringNames.FontColor, textColour);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter })
        {
            EmitSignalConfirmed();
        }
    }

    private void OnConfirmed()
    {
        if (_isNameValid is false) return;
        var directoryName = _nameLineEdit.Text.Trim();
        if (string.IsNullOrEmpty(directoryName))
        {
            GD.PrintErr("Directory name cannot be empty.");
            return;
        }

        _ = Task.GodotRun(async () =>
        {
            await _ideFileOperationsService.RenameDirectory(Folder, directoryName);
        });
        QueueFree();
    }
}