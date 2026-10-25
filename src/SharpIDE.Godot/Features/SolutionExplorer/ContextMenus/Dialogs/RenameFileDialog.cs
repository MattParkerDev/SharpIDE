using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

public partial class RenameFileDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;

    public SharpIdeFile File { get; set; } = null!;
    /// Set when the file is a solution item, so that the sln entry is renamed too
    public SharpIdeSolutionFile? SolutionFile { get; set; }

    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;
    [Inject] private readonly SharpIdeSolutionService _sharpIdeSolutionService = null!;

    private bool _isNameValid = true;
    private string _fileParentPath = null!;

    public override void _Ready()
    {
        _fileParentPath = Path.GetDirectoryName(File.Path)!;
        _nameLineEdit = GetNode<LineEdit>("%FileNameLineEdit");
        _nameLineEdit.Text = File.Name.Value;
        _nameLineEdit.GrabFocus();
        // select the name without the extension
        _nameLineEdit.Select(0, File.Name.Value.LastIndexOf('.'));
        _nameLineEdit.TextChanged += ValidateNewFileName;
        Confirmed += OnConfirmed;
        FocusExited += () =>
        {
	        // work around bug: https://github.com/godotengine/godot/issues/81370
	        Callable.From(GrabFocus).CallDeferred();
        };
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
            var oldFilePath = File.Path;
            await _ideFileOperationsService.RenameFile(File, fileName);
            if (SolutionFile is not null) await _sharpIdeSolutionService.RenameSolutionFile(oldFilePath, fileName);
        });
        QueueFree();
    }
}
