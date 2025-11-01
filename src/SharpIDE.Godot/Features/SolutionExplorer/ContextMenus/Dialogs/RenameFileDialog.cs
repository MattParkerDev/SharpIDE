using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

public partial class RenameFileDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;
    
    public SharpIdeFile File { get; set; } = null!;

    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;
    
    private bool _isNameValid = true;
    private string _fileParentPath = null!;

    public override void _Ready()
    {
        _fileParentPath = Path.GetDirectoryName(File.Path)!;
        _nameLineEdit = GetNode<LineEdit>("%FileNameLineEdit");
        _nameLineEdit.Text = File.Name;
        _nameLineEdit.GrabFocus();
        // select the name without the extension
        _nameLineEdit.Select(0, File.Name.LastIndexOf('.'));
        _nameLineEdit.TextChanged += ValidateNewFileName;
        Confirmed += OnConfirmed;
    }

    private void ValidateNewFileName(string newFileNameText)
    {
        _isNameValid = true;
        var newFileName = newFileNameText.Trim();
        if (string.IsNullOrEmpty(newFileName) || System.IO.File.Exists(Path.Combine(_fileParentPath, newFileName)))
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
        var fileName = _nameLineEdit.Text.Trim();
        if (string.IsNullOrEmpty(fileName))
        {
            GD.PrintErr("File name cannot be empty.");
            return;
        }

        _ = Task.GodotRun(async () =>
        {
            await _ideFileOperationsService.RenameFile(File, fileName);
        });
        QueueFree();
    }
}