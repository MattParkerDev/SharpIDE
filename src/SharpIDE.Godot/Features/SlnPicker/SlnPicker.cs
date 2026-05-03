using Godot;

namespace SharpIDE.Godot.Features.SlnPicker;

// This is a bit of a mess intertwined with the optional popup window
public partial class SlnPicker : Control
{
    private FileDialog _fileDialog = null!;
    private Button _openSlnButton = null!;
    private VBoxContainer _previousSlnsVBoxContainer = null!;
    private Label _versionLabel = null!;
    private Button _aboutButton = null!;

    private PackedScene _previousSlnEntryScene = ResourceLoader.Load<PackedScene>("res://Features/SlnPicker/PreviousSlnEntry.tscn");
    private PackedScene _aboutDialogScene = ResourceLoader.Load<PackedScene>("uid://ojk87rgonxey");

    private readonly TaskCompletionSource<string?> _tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void _ExitTree()
    {
        if (!_tcs.Task.IsCompleted) _tcs.SetResult(null);
    }

    public override void _Ready()
    {
        _previousSlnsVBoxContainer = GetNode<VBoxContainer>("%PreviousSlnsVBoxContainer");
        _versionLabel = GetNode<Label>("%VersionLabel");
        _fileDialog = GetNode<FileDialog>("%FileDialog");
        _openSlnButton = GetNode<Button>("%OpenSlnButton");
        _aboutButton = GetNode<Button>("%AboutButton");
        _openSlnButton.Pressed += () => _fileDialog.PopupCentered();
        _aboutButton.Pressed += () =>
        {
            var aboutDialog = _aboutDialogScene.Instantiate<Window>();
            aboutDialog.CloseRequested += aboutDialog.QueueFree;
            AddChild(aboutDialog);
            aboutDialog.PopupCentered();
        };
        var windowParent = GetParentOrNull<Window>();
        _fileDialog.FileSelected += path => _tcs.SetResult(path);
        windowParent?.CloseRequested += () => _tcs.SetResult(null);
        _versionLabel.Text = $"v{Singletons.SharpIdeVersion.ToNormalizedString()}";
        if (Singletons.AppState.IdeSettings.AutoOpenLastSolution && GetParent() is not Window)
        {
            var lastSln = Singletons.AppState.RecentSlns.LastOrDefault();
            if (lastSln is not null && File.Exists(lastSln.FilePath))
            {
                _tcs.TrySetResult(lastSln.FilePath);
            }
        }
        PopulatePreviousSolutions();
    }
    
    private void PopulatePreviousSolutions()
    {
        _previousSlnsVBoxContainer.QueueFreeChildren();
        foreach (var previousSln in Singletons.AppState.RecentSlns.AsEnumerable().Reverse())
        {
            var node = _previousSlnEntryScene.Instantiate<PreviousSlnEntry>();
            node.RecentSln = previousSln;
            node.Clicked = path => _tcs.TrySetResult(path);
            _previousSlnsVBoxContainer.AddChild(node);
        }
    }

    public async Task<string?> GetSelectedSolutionPath()
    {
        return await _tcs.Task;
    }
}
