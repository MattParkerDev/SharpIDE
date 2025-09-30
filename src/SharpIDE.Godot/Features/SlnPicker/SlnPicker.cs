using Godot;

namespace SharpIDE.Godot.Features.SlnPicker;

// This is a bit of a mess intertwined with the optional popup window
public partial class SlnPicker : Control
{
    private FileDialog _fileDialog = null!;
    private Button _openSlnButton = null!;

    private readonly TaskCompletionSource<string?> _tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void _ExitTree()
    {
        if (!_tcs.Task.IsCompleted) _tcs.SetResult(null);
    }

    public override void _Ready()
    {
        _fileDialog = GetNode<FileDialog>("%FileDialog");
        _openSlnButton = GetNode<Button>("%OpenSlnButton");
        _openSlnButton.Pressed += () => _fileDialog.PopupCentered();
        var windowParent = GetParentOrNull<Window>();
        _fileDialog.FileSelected += path =>
        {
            windowParent?.Hide();
            _tcs.SetResult(path);
        };
        windowParent?.CloseRequested += () => _tcs.SetResult(null);
    }

    public async Task<string?> GetSelectedSolutionPath()
    {
        return await _tcs.Task;
    }
}
