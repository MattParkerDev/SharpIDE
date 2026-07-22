using System.Collections.Specialized;
using Godot;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.Run.RunProjectsComponent;

public partial class RunProjectsComponent : MarginContainer
{
	private Button _runMenuButton = null!;
	private Popup _runMenuPopup = null!;
	private VBoxContainer _runMenuPopupVbox = null!;
	private readonly List<RunMenuItemContainer> _runMenuItemContainers = [];

	private readonly PackedScene _runMenuItemScene = ResourceLoader.Load<PackedScene>("res://Features/Run/RunMenuItem.tscn");

    [Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;
	public override void _Ready()
	{
		_runMenuPopup = GetNode<Popup>("%RunMenuPopup");
		_runMenuButton = GetNode<Button>("%RunMenuButton");
		_runMenuPopupVbox = _runMenuPopup.GetNode<VBoxContainer>("MarginContainer/VBoxContainer");
		_runMenuButton.Pressed += OnRunMenuButtonPressed;
		_ = Task.GodotRun(AsyncReady);
	}

	public override void _ExitTree()
	{
		foreach (var container in _runMenuItemContainers)
		{
			container.ProjectSubscription?.Dispose();
		}
		_runMenuItemContainers.Clear();
	}

	public async Task AsyncReady()
	{
		await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
		var solutionModel = _sharpIdeSolutionAccessor.SolutionModel;
		await this.InvokeAsync(() => BindToProjects(solutionModel.AllProjects));
	}

	[RequiresGodotUiThread]
	private void BindToProjects(ObservableHashSet<SharpIdeProjectModel> projects)
	{
		var projectsView = projects.CreateView(_ => new RunMenuItemContainer());
		foreach (var project in projectsView.Unfiltered.ToList())
			BindProject(project.Value, project.View);

		projectsView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (e, ct) => await this.InvokeAsync(() =>
			{
				switch (e.Action)
				{
					case NotifyCollectionChangedAction.Add:
						BindProject(e.NewItem.Value, e.NewItem.View);
						break;
					case NotifyCollectionChangedAction.Remove:
						UnbindProject(e.OldItem.View);
						break;
				}
			}), configureAwait: false).AddTo(this);
	}

	[RequiresGodotUiThread]
	private void BindProject(SharpIdeProjectModel project, RunMenuItemContainer runMenuItemContainer)
	{
		_runMenuItemContainers.Add(runMenuItemContainer);
		runMenuItemContainer.ProjectSubscription = project.ActiveMsBuildProjectLoadState.SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (loadState, ct) => await this.InvokeAsync(() => UpdateProjectDisplay(project, runMenuItemContainer, loadState)), configureAwait: false);
	}

	[RequiresGodotUiThread]
	private void UpdateProjectDisplay(SharpIdeProjectModel project, RunMenuItemContainer runMenuItemContainer, MsBuildProjectLoadState loadState)
	{
		if (!_runMenuItemContainers.Contains(runMenuItemContainer)) return;

		if (loadState is MsBuildProjectLoadState.Loaded && project.IsRunnable)
		{
			if (runMenuItemContainer.MenuItem is null)
			{
				var runMenuItem = _runMenuItemScene.Instantiate<RunMenuItem>();
				runMenuItem.Project = project;
				_runMenuPopupVbox.AddChild(runMenuItem);
				runMenuItemContainer.MenuItem = runMenuItem;
			}
		}
		else if (runMenuItemContainer.MenuItem is not null)
		{
			runMenuItemContainer.MenuItem.QueueFree();
			runMenuItemContainer.MenuItem = null;
		}

		UpdateRunMenuButton();
	}

	[RequiresGodotUiThread]
	private void UnbindProject(RunMenuItemContainer runMenuItemContainer)
	{
		runMenuItemContainer.ProjectSubscription?.Dispose();
		runMenuItemContainer.MenuItem?.QueueFree();
		_runMenuItemContainers.Remove(runMenuItemContainer);
		UpdateRunMenuButton();
	}

	[RequiresGodotUiThread]
	private void UpdateRunMenuButton()
	{
		_runMenuButton.Disabled = _runMenuItemContainers.All(s => s.MenuItem is null);
		if (_runMenuButton.Disabled)
			_runMenuPopup.Hide();
	}

	private void OnRunMenuButtonPressed()
	{
		var popupMenuPosition = _runMenuButton.GlobalPosition;
		const int buttonHeight = 37;
		_runMenuPopup.Position = new Vector2I((int)popupMenuPosition.X, (int)popupMenuPosition.Y + buttonHeight);
		_runMenuPopup.Popup();
	}

	private sealed class RunMenuItemContainer
	{
		public RunMenuItem? MenuItem { get; set; }
		public IDisposable? ProjectSubscription { get; set; }
	}
}
