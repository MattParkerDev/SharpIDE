using System.Diagnostics;

using Godot;

using Microsoft.Extensions.Logging;

using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.NavigationHistory;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.BottomPanel;
using SharpIDE.Godot.Features.Layout;
using SharpIDE.Godot.Features.Run;
using SharpIDE.Godot.Features.Search;
using SharpIDE.Godot.Features.Search.SearchAllFiles;

using Orientation = SharpIDE.Godot.Features.Layout.Orientation;

namespace SharpIDE.Godot;

public partial class IdeRoot : Control
{
	public IdeWindow IdeWindow { get; set; } = null!;
	
	private Button _openSlnButton = null!;
	private Button _buildSlnButton = null!;
	private Button _rebuildSlnButton = null!;
	private Button _cleanSlnButton = null!;
	private Button _restoreSlnButton = null!;
	private SearchWindow _searchWindow = null!;
	private SearchAllFilesWindow _searchAllFilesWindow = null!;
	private Button _runMenuButton = null!;
	private Popup _runMenuPopup = null!;
	private IdeDockLayout _ideLayoutRoot = null!;

	private IdeDockLayout _dockLayoutRoot = null!;
	
	private readonly PackedScene _runMenuItemScene = ResourceLoader.Load<PackedScene>("res://Features/Run/RunMenuItem.tscn");
	private TaskCompletionSource _nodeReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

	[Inject] private readonly FileChangedService _fileChangedService = null!;
	[Inject] private readonly IdeFileExternalChangeHandler _fileExternalChangeHandler = null!;
	[Inject] private readonly IdeFileWatcher _fileWatcher = null!;
	[Inject] private readonly BuildService _buildService = null!;
	[Inject] private readonly IdeOpenTabsFileManager _openTabsFileManager = null!;
	[Inject] private readonly RoslynAnalysis _roslynAnalysis = null!;
	[Inject] private readonly SharpIdeSolutionModificationService _sharpIdeSolutionModificationService = null!;
	[Inject] private readonly IdeNavigationHistoryService _navigationHistoryService = null!;
	[Inject] private readonly SharpIdeSolutionManager _solutionManager = null!;
	[Inject] private readonly ILogger<IdeRoot> _logger = null!;

	public override void _EnterTree()
	{
		GodotGlobalEvents.Instance = new GodotGlobalEvents();
		GlobalEvents.Instance = new GlobalEvents();
	}

	public override void _ExitTree()
	{
		_fileWatcher?.Dispose();
		GetTree().GetRoot().FocusExited -= OnFocusExited;
	}

	public override void _Ready()
	{
		_openSlnButton = GetNode<Button>("%OpenSlnButton");
		_buildSlnButton = GetNode<Button>("%BuildSlnButton");
		_rebuildSlnButton = GetNode<Button>("%RebuildSlnButton");
		_cleanSlnButton = GetNode<Button>("%CleanSlnButton");
		_restoreSlnButton = GetNode<Button>("%RestoreSlnButton");
		_runMenuPopup = GetNode<Popup>("%RunMenuPopup");
		_runMenuButton = GetNode<Button>("%RunMenuButton");
		_searchWindow = GetNode<SearchWindow>("%SearchWindow");
		_searchAllFilesWindow = GetNode<SearchAllFilesWindow>("%SearchAllFilesWindow");
		_ideLayoutRoot = GetNode<IdeDockLayout>("%LayoutRoot");
		
		_runMenuButton.Pressed += OnRunMenuButtonPressed;
		GodotGlobalEvents.Instance.FileSelected.Subscribe(OnSolutionExplorerPanelOnFileSelected);
		_openSlnButton.Pressed += () => IdeWindow.PickSolution();
		_buildSlnButton.Pressed += OnBuildSlnButtonPressed;
		_rebuildSlnButton.Pressed += OnRebuildSlnButtonPressed;
		_cleanSlnButton.Pressed += OnCleanSlnButtonPressed;
		_restoreSlnButton.Pressed += OnRestoreSlnButtonPressed;
		GetTree().GetRoot().FocusExited += OnFocusExited;
		_nodeReadyTcs.SetResult();
		
		// TODO: Add layout profiles and persist layout
		var ideLayout = new IdeSplitNode(
			Orientation.Vertical,
			FirstNode: new IdeSplitNode(
				Orientation.Horizontal,
				FirstNode: new IdeSceneNode(ResourceHelper.SolutionExplorerId, "Solution Explorer"),
				SecondNode: new IdeSceneNode(ResourceHelper.CodeEditorId, "Code Editor")),
			SecondNode: new IdeSceneNode(ResourceHelper.MultiFunctionPanelId, "Multi Function Panel"));
		
		_ideLayoutRoot.UpdateLayout(ideLayout);
	}
	
	// TODO: Problematic, as this is called even when the focus shifts to an embedded subwindow, such as a popup 
	private void OnFocusExited()
	{
		if (Debugger.IsAttached is false)
		{
			_ = Task.GodotRun(async () => await _openTabsFileManager.SaveAllOpenFilesAsync());
		}
	}

	private void OnRunMenuButtonPressed()
	{
		var popupMenuPosition = _runMenuButton.GlobalPosition;
		const int buttonHeight = 37;
		_runMenuPopup.Position = new Vector2I((int)popupMenuPosition.X, (int)popupMenuPosition.Y + buttonHeight);
		_runMenuPopup.Popup();
	}

	private async void OnBuildSlnButtonPressed()
	{
		await _solutionManager.SolutionReadyTcs.Task;
		
		GodotGlobalEvents.Instance.BottomPanelTabExternallySelected.InvokeParallelFireAndForget(BottomPanelType.Build);
		await _buildService.MsBuildAsync(_solutionManager.SolutionModel.FilePath);
	}
	private async void OnRebuildSlnButtonPressed()
	{
		await _solutionManager.SolutionReadyTcs.Task;
		
		GodotGlobalEvents.Instance.BottomPanelTabExternallySelected.InvokeParallelFireAndForget(BottomPanelType.Build);
		await _buildService.MsBuildAsync(_solutionManager.SolutionModel.FilePath, BuildType.Rebuild);
	}
	private async void OnCleanSlnButtonPressed()
	{
		await _solutionManager.SolutionReadyTcs.Task;
		
		GodotGlobalEvents.Instance.BottomPanelTabExternallySelected.InvokeParallelFireAndForget(BottomPanelType.Build);
		await _buildService.MsBuildAsync(_solutionManager.SolutionModel.FilePath, BuildType.Clean);
	}
	private async void OnRestoreSlnButtonPressed()
	{
		await _solutionManager.SolutionReadyTcs.Task;
		
		GodotGlobalEvents.Instance.BottomPanelTabExternallySelected.InvokeParallelFireAndForget(BottomPanelType.Build);
		await _buildService.MsBuildAsync(_solutionManager.SolutionModel.FilePath, BuildType.Restore);
	}

	private Task OnSolutionExplorerPanelOnFileSelected(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition)
	{
		_navigationHistoryService.RecordNavigation(file, fileLinePosition ?? new SharpIdeFileLinePosition(0, 0));
		return Task.CompletedTask;
	}

	public void SetSlnFilePath(string path)
	{
		_ = Task.GodotRun(async () =>
		{
			GD.Print($"Selected: {path}");
			var timer = Stopwatch.StartNew();
			var solutionModel = await _solutionManager.LoadSolution(path);
			timer.Stop();
			await _nodeReadyTcs.Task;
			// Do not use injected services until after _nodeReadyTcs - Services aren't injected until _Ready
			_logger.LogInformation("Solution model fully created in {ElapsedMilliseconds} ms", timer.ElapsedMilliseconds);
			_searchWindow.Solution = solutionModel;
			_searchAllFilesWindow.Solution = solutionModel;
			_fileExternalChangeHandler.SolutionModel = solutionModel;
			_fileChangedService.SolutionModel = solutionModel;
			_sharpIdeSolutionModificationService.SolutionModel = solutionModel;
			_roslynAnalysis.StartLoadingSolutionInWorkspace(solutionModel);
			_fileWatcher.StartWatching(solutionModel);
			
			var previousTabs = Singletons.AppState.RecentSlns.Single(s => s.FilePath == solutionModel.FilePath).IdeSolutionState.OpenTabs;
			var filesToOpen = previousTabs
				.Select(s => (solutionModel.AllFiles.GetValueOrDefault(s.FilePath), new SharpIdeFileLinePosition(s.CaretLine, s.CaretColumn), s.IsSelected))
				.Where(s => s.Item1 is not null)
				.OfType<(SharpIdeFile file, SharpIdeFileLinePosition linePosition, bool isSelected)>()
				.ToList();
			await this.InvokeDeferredAsync(async () =>
			{
				// Preserves order of tabs
				foreach (var (file, linePosition, isSelected) in filesToOpen)
				{
					await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(file, linePosition);
				}
				_navigationHistoryService.StartRecording();
				// Select the selected tab
				var selectedFile = filesToOpen.SingleOrDefault(f => f.isSelected);
				if (selectedFile.file is not null) await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(selectedFile.file, selectedFile.linePosition);
			});

			var tasks = solutionModel.AllProjects.Select(p => p.MsBuildEvaluationProjectTask).ToList();
			await Task.WhenAll(tasks).ConfigureAwait(false);
			var runnableProjects = solutionModel.AllProjects.Where(p => p.IsRunnable).ToList();
			await this.InvokeAsync(() =>
			{
				var runMenuPopupVbox = _runMenuPopup.GetNode<VBoxContainer>("MarginContainer/VBoxContainer");
				foreach (var project in runnableProjects)
				{
					var runMenuItem = _runMenuItemScene.Instantiate<RunMenuItem>();
					runMenuItem.Project = project;
					runMenuPopupVbox.AddChild(runMenuItem);
				}
				_runMenuButton.Disabled = false;
			});
		});
	}
	
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event.IsActionPressed(InputStringNames.FindInFiles))
		{
			_searchWindow.Popup();
		}
		else if (@event.IsActionPressed(InputStringNames.FindFiles))
		{
			_searchAllFilesWindow.Popup();
		}
	}
}
