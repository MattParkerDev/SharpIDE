using BlazorGodot.Library.V2;
using Godot;
using NuGet.Versioning;
using FileAccess = Godot.FileAccess;
using static BlazorGodot.Library.V2.VNodeExtensions;


namespace SharpIDE.Godot.Features.SlnPicker;

// This is a bit of a mess intertwined with the optional popup window
public partial class SlnPicker : Control
{
    private FileDialog _fileDialog = null!;
    private Button _openSlnButton = null!;
    private VBoxContainer _previousSlnsVBoxContainer = null!;
    private Label _versionLabel = null!;
    private static NuGetVersion? _version;

    private PackedScene _previousSlnEntryScene = ResourceLoader.Load<PackedScene>("res://Features/SlnPicker/PreviousSlnEntry.tscn");

    private readonly TaskCompletionSource<string?> _tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void _ExitTree()
    {
        if (!_tcs.Task.IsCompleted) _tcs.SetResult(null);
    }
    
	private List<string> _myStrings = ["one", "two", "three"];
    private int _counter = 0;
    protected VNode Render() =>
        _<VBoxContainer>()
        [
            _<Label>(s => { s.Text = "Hello, World!"; s.TextDirection = Control.TextDirection.Auto; }),
            _<VBoxContainer>()
            [[
                _<Label>(),
                .. _myStrings.Select(s => _<Label>(l => l.Text = s))
            ]],
            _<Label>(s => { s.Text = _counter.ToString(); }),
            _<Button>(s => { s.Text = "Increment"; s.Pressed += () =>
            {
                _counter++;
                GD.Print($"Counter incremented: {_counter}");
                StateHasChanged();
            }; })
        ];

    private void StateHasChanged()
    {
        this.RemoveChildAndQueueFree(_renderedRoot);
        var vNode = Render();
        _renderedRoot = vNode.Build();
        AddChild(_renderedRoot);
    }

    private VNode _vNodeRoot = null!;
    private Node _renderedRoot = null!;
    public override void _Ready()
    {
        _previousSlnsVBoxContainer = GetNode<VBoxContainer>("%PreviousSlnsVBoxContainer");
        _versionLabel = GetNode<Label>("%VersionLabel");
        _fileDialog = GetNode<FileDialog>("%FileDialog");
        _openSlnButton = GetNode<Button>("%OpenSlnButton");
        _openSlnButton.Pressed += () => _fileDialog.PopupCentered();
        var windowParent = GetParentOrNull<Window>();
        _fileDialog.FileSelected += path => _tcs.SetResult(path);
        windowParent?.CloseRequested += () => _tcs.SetResult(null);
        if (_version is null)
        {
            var version = FileAccess.GetFileAsString("res://version.txt").Trim();
            _version = NuGetVersion.Parse(version);
        }
        _versionLabel.Text = $"v{_version.ToNormalizedString()}";
        if (Singletons.AppState.IdeSettings.AutoOpenLastSolution && GetParent() is not Window)
        {
            var lastSln = Singletons.AppState.RecentSlns.LastOrDefault();
            if (lastSln is not null && File.Exists(lastSln.FilePath))
            {
                _tcs.TrySetResult(lastSln.FilePath);
            }
        }
        PopulatePreviousSolutions();
        _renderedRoot = Render().Build();
        AddChild(_renderedRoot);
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
