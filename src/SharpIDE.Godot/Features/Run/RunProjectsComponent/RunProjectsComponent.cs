using System.Collections.Specialized;
using Godot;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.Run.RunProjectsComponent;

public partial class RunProjectsComponent : MarginContainer
{
	private Button _projectListMenuButton = null!;
	private Button _runButton = null!;
	private Button _debugButton = null!;
	private Popup _runMenuPopup = null!;
	private VBoxContainer _runMenuPopupVbox = null!;
	private readonly List<RunMenuItemContainer> _runMenuItemContainers = [];
	private RunMenuItemContainer? _activeRunMenuItemContainer;
	private IDisposable? _activeProjectNameSubscription;

	private readonly PackedScene _runMenuItemScene = ResourceLoader.Load<PackedScene>("res://Features/Run/RunMenuItem.tscn");

    [Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;
	public override void _Ready()
	{
		_runMenuPopup = GetNode<Popup>("%RunMenuPopup");
		_projectListMenuButton = GetNode<Button>("%ProjectListMenuButton");
		_runButton = GetNode<Button>("%RunButton");
		_debugButton = GetNode<Button>("%DebugButton");
		_runMenuPopupVbox = _runMenuPopup.GetNode<VBoxContainer>("MarginContainer/VBoxContainer");
		_runMenuPopup.PopupHide += OnRunMenuPopupHidden;
		_projectListMenuButton.Pressed += OnProjectListMenuButtonPressed;
		_runButton.Pressed += OnRunButtonPressed;
		_debugButton.Pressed += OnDebugButtonPressed;
		_ = Task.GodotRun(AsyncReady);
	}

	public override void _ExitTree()
	{
		_activeProjectNameSubscription?.Dispose();
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
				runMenuItem.Selected += OnRunMenuItemSelected;
				_runMenuPopupVbox.AddChild(runMenuItem);
				runMenuItemContainer.MenuItem = runMenuItem;
				if (_activeRunMenuItemContainer is null) SetActiveRunMenuItem(runMenuItemContainer);
			}
		}
		else if (runMenuItemContainer.MenuItem is not null)
		{
			// Set active to a different project since this one is no longer runnable
			if (_activeRunMenuItemContainer == runMenuItemContainer)
			{
				SetActiveRunMenuItem(GetFirstAvailableRunMenuItem(runMenuItemContainer));
			}
			runMenuItemContainer.MenuItem.QueueFree();
			runMenuItemContainer.MenuItem = null;
		}

		UpdateRunMenuButton();
	}

	[RequiresGodotUiThread]
	private void UnbindProject(RunMenuItemContainer runMenuItemContainer)
	{
		runMenuItemContainer.ProjectSubscription?.Dispose();
		if (_activeRunMenuItemContainer == runMenuItemContainer)
			SetActiveRunMenuItem(GetFirstAvailableRunMenuItem(runMenuItemContainer));
		runMenuItemContainer.MenuItem?.QueueFree();
		_runMenuItemContainers.Remove(runMenuItemContainer);
		UpdateRunMenuButton();
	}

	[RequiresGodotUiThread]
	private RunMenuItemContainer? GetFirstAvailableRunMenuItem(RunMenuItemContainer? excluded = null)
	{
		return _runMenuItemContainers.FirstOrDefault(container => container != excluded && container.MenuItem is not null);
	}

	[RequiresGodotUiThread]
	private void OnRunMenuItemSelected(RunMenuItem runMenuItem)
	{
		var container = _runMenuItemContainers.Single(container => container.MenuItem == runMenuItem);
		SetActiveRunMenuItem(container);
		_runMenuPopup.Hide();
	}

	[RequiresGodotUiThread]
	private void SetActiveRunMenuItem(RunMenuItemContainer? container)
	{
		_activeProjectNameSubscription?.Dispose();
		_activeProjectNameSubscription = null;
		_activeRunMenuItemContainer = container;

		if (container?.MenuItem is not { } menuItem)
		{
			_projectListMenuButton.Text = "No runnable projects";
			return;
		}

		_projectListMenuButton.Text = menuItem.Project.Name.Value;
		_activeProjectNameSubscription = menuItem.Project.Name.Skip(1).SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (name, ct) => await this.InvokeAsync(() =>
			{
				if (_activeRunMenuItemContainer == container)
					_projectListMenuButton.Text = name;
			}), configureAwait: false);
	}

	[RequiresGodotUiThread]
	private void UpdateRunMenuButton()
	{
		_projectListMenuButton.Disabled = _runMenuItemContainers.All(s => s.MenuItem is null);
		var disabled = _activeRunMenuItemContainer?.MenuItem is null;
		_runButton.Disabled = disabled;
		_debugButton.Disabled = disabled;
		if (_projectListMenuButton.Disabled)
			_runMenuPopup.Hide();
	}

	private void OnProjectListMenuButtonPressed()
	{
		if (_projectListMenuButton.ButtonPressed is false)
		{
			_runMenuPopup.Hide();
			return;
		}
		var popupMenuPosition = _projectListMenuButton.GlobalPosition;
		const int buttonHeight = 37;
		_runMenuPopup.Position = new Vector2I((int)popupMenuPosition.X, (int)popupMenuPosition.Y + buttonHeight);
		_runMenuPopup.Popup();
	}

	private void OnRunMenuPopupHidden()
	{
		// A click on the toggle will turn it off on mouse-up. Other dismissals need to update the toggle state
		if (!_projectListMenuButton.GetGlobalRect().HasPoint(GetGlobalMousePosition()))
			_projectListMenuButton.ButtonPressed = false;
	}

	private async void OnRunButtonPressed()
	{
		if (_activeRunMenuItemContainer?.MenuItem is { } menuItem)
			await menuItem.RunProject().ConfigureAwait(false);
	}

	private async void OnDebugButtonPressed()
	{
		if (_activeRunMenuItemContainer?.MenuItem is { } menuItem)
			await menuItem.DebugProject().ConfigureAwait(false);
	}

	private sealed class RunMenuItemContainer
	{
		public RunMenuItem? MenuItem { get; set; }
		public IDisposable? ProjectSubscription { get; set; }
	}
}
