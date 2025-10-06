using Godot;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Hosting;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.BottomPanel;
using SharpIDE.Godot.Features.CodeEditor;
using SharpIDE.Godot.Features.CustomControls;
using SharpIDE.Godot.Features.Run;
using SharpIDE.Godot.Features.Search;
using SharpIDE.Godot.Features.SolutionExplorer;

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
	private CodeEditorPanel _codeEditorPanel = null!;
	private SolutionExplorerPanel _solutionExplorerPanel = null!;
	private InvertedVSplitContainer _invertedVSplitContainer = null!;
	private RunPanel _runPanel = null!;
	private Button _runMenuButton = null!;
	private Popup _runMenuPopup = null!;
	private BottomPanelManager _bottomPanelManager = null!;
	
	private readonly PackedScene _runMenuItemScene = ResourceLoader.Load<PackedScene>("res://Features/Run/RunMenuItem.tscn");
	private TaskCompletionSource _nodeReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

	public override void _EnterTree()
	{
		GodotGlobalEvents.Instance = new GodotGlobalEvents();
		GlobalEvents.Instance = new GlobalEvents();
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
		_codeEditorPanel = GetNode<CodeEditorPanel>("%CodeEditorPanel");
		_searchWindow = GetNode<SearchWindow>("%SearchWindow");
		_solutionExplorerPanel = GetNode<SolutionExplorerPanel>("%SolutionExplorerPanel");
		_runPanel = GetNode<RunPanel>("%RunPanel");
		_invertedVSplitContainer = GetNode<InvertedVSplitContainer>("%InvertedVSplitContainer");
		_bottomPanelManager = GetNode<BottomPanelManager>("%BottomPanel");
		
		_runMenuButton.Pressed += OnRunMenuButtonPressed;
		GodotGlobalEvents.Instance.FileSelected += OnSolutionExplorerPanelOnFileSelected;
		_openSlnButton.Pressed += () => IdeWindow.PickSolution();
		_buildSlnButton.Pressed += OnBuildSlnButtonPressed;
		_rebuildSlnButton.Pressed += OnRebuildSlnButtonPressed;
		_cleanSlnButton.Pressed += OnCleanSlnButtonPressed;
		_restoreSlnButton.Pressed += OnRestoreSlnButtonPressed;
		GodotGlobalEvents.Instance.BottomPanelVisibilityChangeRequested += async show => await this.InvokeAsync(() => _invertedVSplitContainer.InvertedSetCollapsed(!show));
		_nodeReadyTcs.SetResult();
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
		GodotGlobalEvents.Instance.InvokeBottomPanelTabExternallySelected(BottomPanelType.Build);
		await Singletons.BuildService.MsBuildSolutionAsync(_solutionExplorerPanel.SolutionModel.FilePath);
	}
	private async void OnRebuildSlnButtonPressed()
	{
		GodotGlobalEvents.Instance.InvokeBottomPanelTabExternallySelected(BottomPanelType.Build);
		await Singletons.BuildService.MsBuildSolutionAsync(_solutionExplorerPanel.SolutionModel.FilePath, BuildType.Rebuild);
	}
	private async void OnCleanSlnButtonPressed()
	{
		GodotGlobalEvents.Instance.InvokeBottomPanelTabExternallySelected(BottomPanelType.Build);
		await Singletons.BuildService.MsBuildSolutionAsync(_solutionExplorerPanel.SolutionModel.FilePath, BuildType.Clean);
	}
	private async void OnRestoreSlnButtonPressed()
	{
		GodotGlobalEvents.Instance.InvokeBottomPanelTabExternallySelected(BottomPanelType.Build);
		await Singletons.BuildService.MsBuildSolutionAsync(_solutionExplorerPanel.SolutionModel.FilePath, BuildType.Restore);
	}

	private async Task OnSolutionExplorerPanelOnFileSelected(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition)
	{
		await _codeEditorPanel.SetSharpIdeFile(file, fileLinePosition);
	}

	public void SetSlnFilePath(string path)
	{
		_ = Task.GodotRun(async () =>
		{
			GD.Print($"Selected: {path}");
			var solutionModel = await VsPersistenceMapper.GetSolutionModel(path);
			await _nodeReadyTcs.Task;
			_solutionExplorerPanel.SolutionModel = solutionModel;
			_codeEditorPanel.Solution = solutionModel;
			_bottomPanelManager.Solution = solutionModel;
			_searchWindow.Solution = solutionModel;
			Callable.From(_solutionExplorerPanel.RepopulateTree).CallDeferred();
			RoslynAnalysis.StartSolutionAnalysis(solutionModel);
			
			var infraProject = solutionModel.AllProjects.SingleOrDefault(s => s.Name == "WebUi");
			var diFile = infraProject?.Folders.Single(s => s.Name == "Pages").Files.Single(s => s.Name == "TestPage.razor");
			if (diFile != null) await this.InvokeDeferredAsync(() => GodotGlobalEvents.Instance.InvokeFileExternallySelected(diFile));
				
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
	}
}