using Godot;
using NuGet.Versioning;
using SharpIDE.Godot.Features.SlnPicker.GodotMarkup;
using FileAccess = Godot.FileAccess;
using static SharpIDE.Godot.Features.SlnPicker.GodotMarkup.VNodeExtensions;


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
            }; }),
            _<VBoxContainer>()
            [
                Enumerable.Range(0, _counter).Select(s => _<Label>(l => l.Text = $"Item {s}"))
            ]
        ];

    private void StateHasChanged()
    {
        var newVNode = Render();
        // if (_renderedRoot is not null)
        // {
        //     this.RemoveChildAndQueueFree(_renderedRoot);
        // }
        // var vNode = Render();
        // _renderedRoot = vNode.Build();
        // AddChild(_renderedRoot);
        // return;
        if (_previousVNode == null || _renderedRoot == null)
        {
            // Initial render
            _renderedRoot = newVNode.Build();
            AddChild(_renderedRoot);
            _mapping.Map(newVNode, _renderedRoot);
            MapAllDescendants(newVNode, _renderedRoot);
        }
        else
        {
            // Diff and patch
            var operations = VNodeDiffer.ComputeDiff(_previousVNode, newVNode, _mapping);
            //VNodePatcher.ApplyDiff(this, operations, _mapping);
            VNodePatcher.ApplyDiff(_renderedRoot, operations, _mapping);
        }

        _previousVNode = newVNode;
    }

    private void MapAllDescendants(VNode vNode, Node node)
    {
        for (int i = 0; i < vNode.Children.Count; i++)
        {
            var childVNode = vNode.Children[i];
            var childNode = node.GetChild(i);
            _mapping.Map(childVNode, childNode);
            MapAllDescendants(childVNode, childNode);
        }
    }

    private VNode?  _previousVNode = null;
    private Node? _renderedRoot = null;
    private NodeMapping _mapping = new();
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
        StateHasChanged();
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
