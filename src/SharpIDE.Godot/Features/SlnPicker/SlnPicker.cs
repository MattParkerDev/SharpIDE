using Godot;
using NuGet.Versioning;
using FileAccess = Godot.FileAccess;

namespace SharpIDE.Godot.Features.SlnPicker;

// This is a bit of a mess intertwined with the optional popup window
public partial class SlnPicker : Control
{
	private FileDialog _fileDialog = null!;
	private AcceptDialog _solutionDialog = null!;
	private Button _newSolutionButton = null!;
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

	public override void _Ready()
	{
		_previousSlnsVBoxContainer = GetNode<VBoxContainer>("%PreviousSlnsVBoxContainer");
		_versionLabel = GetNode<Label>("%VersionLabel");
		_fileDialog = GetNode<FileDialog>("%FileDialog");
		_solutionDialog = GetNode<AcceptDialog>("%SolutionDialog");
		_newSolutionButton = GetNode<Button>("%NewSolutionButton");
		_newSolutionButton.Pressed += () => _solutionDialog.PopupCentered();
		_solutionDialog.Confirmed += () => 
		{
			var solutionName = _solutionDialog.GetNode<LineEdit>("%SlnNameLineEdit").Text;
			var projectName = _solutionDialog.GetNode<LineEdit>("%PrjNameLineEdit").Text;
			var solutionDir = _solutionDialog.GetNode<LineEdit>("%SlnDirLineEdit").Text;
			var projectTemplate = _solutionDialog.GetNode<Label>("%PrjTypeLabel").Text;
			var sdkVersion = _solutionDialog.GetNode<OptionButton>("%SdkVersionOptions").Text;
			
			var creatorNode = new Node();
			var projectCreator = new ProjectCreator();
			creatorNode.AddChild(projectCreator);
			
			projectCreator.CreateProject(solutionDir, solutionName, projectName, projectTemplate, "C#", sdkVersion);
		};
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
