using Godot;

namespace SharpIDE.Godot.Features.SlnPicker;

// This is a bit of a mess intertwined with the optional popup window
public partial class SlnPicker : Control
{
    private FileDialog _fileDialog = null!;
    private Button _openSlnButton = null!;
    private VBoxContainer _previousSlnsVBoxContainer = null!;

    private PackedScene _previousSlnEntryScene = ResourceLoader.Load<PackedScene>("res://Features/SlnPicker/PreviousSlnEntry.tscn");

    private readonly TaskCompletionSource<string?> _tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void _ExitTree()
    {
        if (!_tcs.Task.IsCompleted) _tcs.SetResult(null);
    }

    public override void _Ready()
    {
        _previousSlnsVBoxContainer = GetNode<VBoxContainer>("%PreviousSlnsVBoxContainer");
        _fileDialog = GetNode<FileDialog>("%FileDialog");
        _openSlnButton = GetNode<Button>("%OpenSlnButton");
        _openSlnButton.Pressed += () => _fileDialog.PopupCentered();
        var windowParent = GetParentOrNull<Window>();
        _fileDialog.FileSelected += path => _tcs.SetResult(path);
        windowParent?.CloseRequested += () => _tcs.SetResult(null);
        PopulatePreviousSolutions();
    }
    
    private void PopulatePreviousSolutions()
    {
        _previousSlnsVBoxContainer.QueueFreeChildren();
        foreach (var previousSln in Singletons.AppState.RecentSlns.Reverse())
        {
            var node = _previousSlnEntryScene.Instantiate<PreviousSlnEntry>();
            node.RecentSln = previousSln;
            node.Clicked = path => _tcs.SetResult(path);
            _previousSlnsVBoxContainer.AddChild(node);
        }
    }

    public async Task<string?> GetSelectedSolutionPath()
    {
        return await _tcs.Task;
    }
}
